using RasterSharp.Formats;
using RasterSharp.Formats.WebP;

namespace RasterSharp;

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
        return image.ToPixelData(format);
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
        image.CopyPixelDataTo(output, format, stride);
        return image.Size;
    }

    /// <summary>Decodes WebP data to an <see cref="Image"/> (all frames of animations).</summary>
    public static Image DecodeImage(ReadOnlySpan<byte> data, DecoderOptions? options = null)
    {
        if (FormatDetector.Detect(data) != ImageFormat.WebP)
        {
            throw new ImageFormatException(ImageFormat.WebP, "The data is not a WebP file.");
        }

        return Image.Load(data, options);
    }

    /// <summary>Encodes an image with the supplied WebP options.</summary>
    public static byte[] Encode(Image image, WebPEncoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.ToWebP(options);
    }

    /// <summary>Encodes RGBA pixels losslessly, preserving RGB under transparent pixels.</summary>
    public static byte[] EncodeLosslessRgba(ReadOnlySpan<byte> rgba, int width, int height, int stride = 0)
    {
        using Image image = Image.LoadPixelData(rgba, width, height, PixelFormat.Rgba32, stride);
        return image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
    }

    /// <summary>Converts a supported image file to WebP.</summary>
    public static void Convert(string inputPath, string outputPath, WebPEncoderOptions? options = null)
    {
        using Image image = Image.Load(inputPath);
        image.SaveAsWebP(outputPath, options);
    }
}
