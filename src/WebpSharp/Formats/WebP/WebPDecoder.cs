using System.Drawing;
using System.Runtime.InteropServices;
using WebpSharp.Formats.Metadata;
using WebpSharp.Formats.WebP.Container;
using WebpSharp.Formats.WebP.Lossless;

namespace WebpSharp.Formats.WebP;

/// <summary>Decodes WebP files: still lossy/lossless images and animations.</summary>
internal sealed class WebPDecoder : IImageDecoder
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

        if (c.IsAnimated)
        {
            throw new NotSupportedException("Decoding animated WebP images is not supported yet.");
        }

        ImageFrame frame = DecodeFrame(data, c.Frames[0], options);
        var image = new Image([frame])
        {
            RawFormat = ImageFormat.WebP,
            HasAlpha = c.HasAlpha,
        };

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
        if (!info.IsLossless)
        {
            throw new NotSupportedException("Decoding lossy (VP8) WebP images is not supported yet.");
        }

        (byte[] array, int offset) = GetArray(data);
        uint[] argb = Vp8LDecoder.Decode(array, offset + info.Image.Offset, info.Image.Length, out int width, out int height);
        ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
        ArgbToRgba(argb, frame.GetPixelSpan());
        return frame;
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
        for (int i = 0; i < dst.Length; i++)
        {
            uint p = argb[i];
            dst[i] = new Rgba32((byte)(p >> 16), (byte)(p >> 8), (byte)p, (byte)(p >> 24));
        }
    }

    internal static Color ToColor(uint bgra) =>
        Color.FromArgb((byte)(bgra >> 24), (byte)(bgra >> 16), (byte)(bgra >> 8), (byte)bgra);
}
