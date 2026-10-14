using System.Runtime.InteropServices;
using RasterSharp.Formats.Metadata;
using RasterSharp.Formats.WebP.Alpha;
using RasterSharp.Formats.WebP.Animation;
using RasterSharp.Formats.WebP.ColorConversion;
using RasterSharp.Formats.WebP.Container;
using RasterSharp.Formats.WebP.Lossless;
using RasterSharp.Formats.WebP.Lossy;

namespace RasterSharp.Formats.WebP;

/// <summary>Decodes WebP files: still lossy/lossless images and animations.</summary>
internal sealed partial class WebPDecoder : IImageDecoder
{
    public static WebPDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.WebP;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        WebPContainer c = WebPContainer.Parse(data.Span);
        ExifOrientation orientation = c.Exif.IsEmpty
            ? ExifOrientation.Unknown
            : ExifReader.GetOrientation(c.Exif.Slice(data.Span));
        return new ImageInfo
        {
            Format = ImageFormat.WebP,
            Width = c.CanvasWidth,
            Height = c.CanvasHeight,
            HasAlpha = c.HasAlpha,
            FrameCount = c.Frames.Count,
            IsAnimated = c.IsAnimated,
            WebPCompression = GetCompression(c),
            Orientation = orientation,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        WebPContainer c = WebPContainer.Parse(data.Span);
        options.ValidateDimensions(ImageFormat.WebP, c.CanvasWidth, c.CanvasHeight);

        Image image;
        if (c.IsAnimated)
        {
            image = new Image(AnimationDecoder.Decode(data, c, options))
            {
                LoopCount = c.LoopCount,
                AnimationBackgroundColor = Rgba32.FromArgb(c.BackgroundColor),
            };
        }
        else
        {
            image = new Image([DecodeFrame(data, c.Frames[0], options)]);
        }

        image.SourceFormat = ImageFormat.WebP;
        image.HasAlpha = c.HasAlpha;

        if (!options.IgnoreMetadata)
        {
            ReadMetadata(data.Span, c, image.Metadata);
        }

        return image;
    }

    private static WebPCompression GetCompression(WebPContainer c)
    {
        bool lossless = false;
        bool lossy = false;
        foreach (WebPFrameInfo f in c.Frames)
        {
            lossless |= f.IsLossless;
            lossy |= !f.IsLossless;
        }

        return lossless && lossy ? WebPCompression.Mixed : lossless ? WebPCompression.Lossless : WebPCompression.Lossy;
    }

    private static void ReadMetadata(ReadOnlySpan<byte> data, WebPContainer c, ImageMetadata metadata)
    {
        if (!c.Icc.IsEmpty)
        {
            metadata.IccProfile = c.Icc.Slice(data).ToArray();
        }

        if (!c.Exif.IsEmpty)
        {
            metadata.Exif = c.Exif.Slice(data).ToArray();
        }

        if (!c.Xmp.IsEmpty)
        {
            metadata.Xmp = c.Xmp.Slice(data).ToArray();
        }
    }

    /// <summary>Decodes the bitstream of one frame to a frame-sized RGBA buffer.</summary>
    internal static ImageFrame DecodeFrame(ReadOnlyMemory<byte> data, WebPFrameInfo info, DecoderOptions options)
    {
        (byte[] array, int offset) = GetArray(data);
        if (info.IsLossless)
        {
            uint[] argb = Vp8LDecoder.Decode(array, offset + info.Image.Offset, info.Image.Length, out int width, out int height);
            ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
            ArgbToRgba(argb, frame.GetPixelSpan());
            return frame;
        }
        else
        {
            Vp8Frame yuv = Vp8Decoder.Decode(array, offset + info.Image.Offset, info.Image.Length, options.WebPBypassFiltering, options.MaxDegreeOfParallelism);
            ImageFrame frame = ImageFrame.CreateUninitialized(yuv.Width, yuv.Height);
            Span<Rgba32> pixels = frame.GetPixelSpan();
            YuvToRgb.Convert(yuv, frame.Pixels, options.WebPFancyUpsampling, options.MaxDegreeOfParallelism);
            if (!info.Alpha.IsEmpty)
            {
                byte[] alpha = AlphaDecoder.Decode(array, offset + info.Alpha.Offset, info.Alpha.Length, yuv.Width, yuv.Height);
                for (int i = 0; i < pixels.Length; i++)
                {
                    pixels[i].A = alpha[i];
                }
            }

            return frame;
        }
    }

    private static (byte[] Array, int Offset) GetArray(ReadOnlyMemory<byte> data)
    {
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment))
        {
            return (segment.Array!, segment.Offset);
        }

        return (data.ToArray(), 0);
    }

    internal static void ArgbToRgba(ReadOnlySpan<uint> argb, Span<Rgba32> dst)
    {
        int done = ArgbToRgbaSimd(argb, dst);
        ArgbToRgbaScalar(argb[done..], dst[done..]);
    }

    internal static void ArgbToRgbaScalar(ReadOnlySpan<uint> argb, Span<Rgba32> dst)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            uint p = argb[i];
            dst[i] = new Rgba32((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24));
        }
    }
}
