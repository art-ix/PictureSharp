using System.Buffers.Binary;
using PictureSharp.Domain.Pixels;
using PictureSharp.Formats.Png;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngEncoder
{
    /// <summary>
    /// Resolves <see cref="PngEncoderOptions.BitDepth"/> for the frames to write. Fails before anything is written when
    /// an explicit palette is requested at depth 16.
    /// </summary>
    private static bool UsesSixteenBits(List<ImageFrame> frames, PixelType pixelType, PngEncoderOptions options)
    {
        bool sixteen = options.BitDepth switch
        {
            PngBitDepth.UpTo8 => false,
            PngBitDepth.Sixteen => true,
            _ => pixelType == PixelType.Rgba64 && !frames.TrueForAll(f => PixelDepth.IsExact8Bit(f.GetPixelSpan<Rgba64>())),
        };
        if (sixteen && options.ColorType == PngColorType.Palette)
        {
            throw new ArgumentException(
                "An indexed PNG has at most 8 bits per sample; use PngBitDepth.UpTo8 to reduce a 16-bit image to a palette.", nameof(options));
        }

        return sixteen;
    }

    /// <summary>A 16-bit layout (color types 0, 2, 4 or 6) with grey and opacity detection on 16-bit samples.</summary>
    private static Layout ChooseLayout16(List<ImageFrame> frames, PngColorType requested)
    {
        PngColorType type = requested;
        if (type == PngColorType.Auto)
        {
            bool hasAlpha = false;
            bool isGray = true;
            foreach (ImageFrame frame in frames)
            {
                if (frame.PixelType == PixelType.Rgba32)
                {
                    (bool alpha, bool gray) = ScanAlphaAndGray(frame.Pixels);
                    hasAlpha |= alpha;
                    isGray &= gray;
                    continue;
                }

                foreach (Rgba64 p in frame.GetPixelSpan<Rgba64>())
                {
                    hasAlpha |= p.A != ushort.MaxValue;
                    isGray &= p.R == p.G && p.G == p.B;
                }
            }

            type = isGray ? (hasAlpha ? PngColorType.GrayscaleAlpha : PngColorType.Grayscale)
                : hasAlpha ? PngColorType.Rgba : PngColorType.Rgb;
        }

        PngColor color = type switch
        {
            PngColorType.Grayscale => PngColor.Gray,
            PngColorType.GrayscaleAlpha => PngColor.GrayAlpha,
            PngColorType.Rgb => PngColor.Rgb,
            _ => PngColor.Rgba,
        };
        return new Layout { Color = color, BitDepth = 16 };
    }

    /// <summary>Packs row <paramref name="j"/> of a pass as big-endian 16-bit samples; 8-bit frames are widened exactly.</summary>
    private static void PackRow16(Pass pass, int j, Rgba64[] scratch, Span<byte> dst)
    {
        int y = pass.Y0 + (j * pass.Dy);
        Span<Rgba64> pixels = scratch.AsSpan(0, pass.Width);
        if (pass.Frame.PixelType == PixelType.Rgba64)
        {
            Span<Rgba64> source = pass.Frame.GetRowSpan<Rgba64>(y);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = source[pass.X0 + (i * pass.Dx)];
            }
        }
        else
        {
            Span<Rgba32> source = pass.Frame.PixelRow(y);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = Rgba64.FromRgba32(source[pass.X0 + (i * pass.Dx)]);
            }
        }

        switch (pass.Layout.Color)
        {
            case PngColor.Gray:
                for (int i = 0; i < pixels.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(2 * i)..], Gray16(pixels[i]));
                }

                break;
            case PngColor.GrayAlpha:
                for (int i = 0; i < pixels.Length; i++)
                {
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(4 * i)..], Gray16(pixels[i]));
                    BinaryPrimitives.WriteUInt16BigEndian(dst[((4 * i) + 2)..], pixels[i].A);
                }

                break;
            case PngColor.Rgb:
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba64 p = pixels[i];
                    BinaryPrimitives.WriteUInt16BigEndian(dst[(6 * i)..], p.R);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[((6 * i) + 2)..], p.G);
                    BinaryPrimitives.WriteUInt16BigEndian(dst[((6 * i) + 4)..], p.B);
                }

                break;
            default:
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba64 p = pixels[i];
                    BinaryPrimitives.WriteUInt64BigEndian(dst[(8 * i)..], ((ulong)p.R << 48) | ((ulong)p.G << 32) | ((ulong)p.B << 16) | p.A);
                }

                break;
        }
    }

    private static ushort Gray16(Rgba64 p) => p.R == p.G && p.G == p.B ? p.R : PixelConverter.Luma16(p.R, p.G, p.B);

    /// <summary>
    /// The frame a sequential writer stores at the depth chosen from the first frame: 16-bit frames are reduced for an
    /// 8-bit layout only with <see cref="PngBitDepth.UpTo8"/> or when exact; 8-bit frames are widened while packing.
    /// </summary>
    private static ImageFrame AtLayoutDepth(ImageFrame frame, Layout layout, PngEncoderOptions options)
    {
        if (layout.BitDepth == 16 || frame.PixelType == PixelType.Rgba32)
        {
            return frame;
        }

        if (options.BitDepth != PngBitDepth.UpTo8 && !PixelDepth.IsExact8Bit(frame.GetPixelSpan<Rgba64>()))
        {
            throw new ArgumentException(
                "The PNG sequence was started with 8 bits per sample from its first frame; write it with PngBitDepth.Sixteen "
                + "or UpTo8 to store 16-bit frames.", nameof(frame));
        }

        return Application.Encoding.EncoderInput.ToRgba32(frame);
    }
}
