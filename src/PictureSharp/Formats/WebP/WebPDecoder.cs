using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Formats.Metadata;
using PictureSharp.Formats.WebP.Alpha;
using PictureSharp.Formats.WebP.Animation;
using PictureSharp.Formats.WebP.ColorConversion;
using PictureSharp.Formats.WebP.Container;
using PictureSharp.Formats.WebP.Lossless;
using PictureSharp.Formats.WebP.Lossy;

namespace PictureSharp.Formats.WebP;

/// <summary>Decodes WebP files: still lossy/lossless images and animations.</summary>
internal sealed partial class WebPDecoder : IImageDecoder
{
    public static WebPDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.WebP;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        WebPContainer c = WebPContainer.Parse(data.Span, options);
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

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken)
    {
        WebPContainer c = WebPContainer.Parse(data.Span, options);
        if (!options.IgnoreMetadata)
        {
            options.AddMetadata(ImageFormat.WebP, 0, (long)c.Icc.Length + c.Exif.Length + c.Xmp.Length);
        }

        options.ValidateFrames(ImageFormat.WebP, c.CanvasWidth, c.CanvasHeight,
            c.IsAnimated && !options.FirstFrameOnly ? c.Frames.Count : 1);

        Image image;
        if (c.IsAnimated)
        {
            image = new Image(AnimationDecoder.Decode(data, c, options, cancellationToken))
            {
                LoopCount = c.LoopCount,
                AnimationBackgroundColor = Rgba32.FromArgb(c.BackgroundColor),
            };
        }
        else
        {
            image = new Image([DecodeFrame(data, c.Frames[0], options, cancellationToken)]);
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
    internal static ImageFrame DecodeFrame(ReadOnlyMemory<byte> data, WebPFrameInfo info, DecoderOptions options,
        CancellationToken cancellationToken)
    {
        (byte[] array, int offset) = GetArray(data);
        if (info.IsLossless)
        {
            uint[] argb = Vp8LDecoder.Decode(array, offset + info.Image.Offset, info.Image.Length, out int width, out int height,
                cancellationToken);
            try
            {
                ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
                ArgbToRgba(argb.AsSpan(0, width * height), frame.GetPixelSpan());
                return frame;
            }
            finally
            {
                ArrayPool<uint>.Shared.Return(argb);
            }
        }
        else
        {
            Vp8Frame yuv = Vp8Decoder.Decode(array, offset + info.Image.Offset, info.Image.Length, options.WebPBypassFiltering,
                options.MaxDegreeOfParallelism, cancellationToken);
            ImageFrame frame = ImageFrame.CreateUninitialized(yuv.Width, yuv.Height);
            Span<Rgba32> pixels = frame.GetPixelSpan();
            try
            {
                YuvToRgb.Convert(yuv, frame.Pixels, options.WebPFancyUpsampling, options.MaxDegreeOfParallelism, cancellationToken: cancellationToken);
            }
            finally
            {
                yuv.Return();
            }

            if (!info.Alpha.IsEmpty)
            {
                byte[] alpha = AlphaDecoder.Decode(array, offset + info.Alpha.Offset, info.Alpha.Length, yuv.Width, yuv.Height, cancellationToken);
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
