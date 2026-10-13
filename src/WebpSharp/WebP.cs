using WebpSharp.Formats;
using WebpSharp.Formats.WebP;

namespace WebpSharp;

/// <summary>
/// Simple one-call WebP functions, mirroring libwebp's decode.h / encode.h API
/// (WebPGetInfo, WebPDecodeRGBA, ...). For full control use <see cref="Image"/>.
/// </summary>
public static class WebP
{
    private static readonly DecoderOptions FirstFrame = new() { FirstFrameOnly = true };

    /// <summary>Reads the basic properties of WebP data without decoding pixels.</summary>
    /// <returns>False when the data is not a valid WebP file.</returns>
    public static bool TryGetInfo(ReadOnlySpan<byte> data, out ImageInfo info)
    {
        info = null!;
        if (FormatDetector.Detect(data) != ImageFormat.WebP)
        {
            return false;
        }

        try
        {
            info = WebPDecoder.Instance.Identify(data.ToArray(), DecoderOptions.Default);
            return true;
        }
        catch (ImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Decodes WebP data (the first frame of animations) to RGBA bytes, like WebPDecodeRGBA.</summary>
    public static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height) =>
        Decode(data, PixelFormat.Rgba32, out width, out height);

    /// <summary>Decodes WebP data (the first frame of animations) to BGRA bytes, like WebPDecodeBGRA.</summary>
    public static byte[] DecodeBgra(ReadOnlySpan<byte> data, out int width, out int height) =>
        Decode(data, PixelFormat.Bgra32, out width, out height);

    /// <summary>Decodes WebP data (the first frame of animations) to bytes in the given layout.</summary>
    public static byte[] Decode(ReadOnlySpan<byte> data, PixelFormat format, out int width, out int height)
    {
        using Image image = DecodeImage(data, FirstFrame);
        width = image.Width;
        height = image.Height;
        return image.ToPixelArray(format);
    }

    /// <summary>Decodes WebP data into a caller-provided buffer, like WebPDecodeRGBAInto.</summary>
    /// <param name="data">Encoded WebP data.</param>
    /// <param name="output">Destination buffer; must hold <paramref name="stride"/> × height bytes.</param>
    /// <param name="stride">Bytes per row in <paramref name="output"/>; 0 means tightly packed.</param>
    /// <param name="format">Layout to write.</param>
    /// <returns>The image dimensions.</returns>
    public static System.Drawing.Size DecodeInto(ReadOnlySpan<byte> data, Span<byte> output, int stride, PixelFormat format)
    {
        using Image image = DecodeImage(data, FirstFrame);
        image.CopyPixels(output, format, stride);
        return image.Size;
    }

    /// <summary>Decodes WebP data to an <see cref="Image"/> (all frames of animations).</summary>
    public static Image DecodeImage(ReadOnlySpan<byte> data, DecoderOptions? options = null)
    {
        if (FormatDetector.Detect(data) != ImageFormat.WebP)
        {
            throw new ImageFormatException(ImageFormat.WebP, "The data is not a WebP file.");
        }

        return Image.FromBytes(data, options);
    }
}
