using RasterSharp.Formats.Bmp;
using RasterSharp.Formats.Jpeg;
using RasterSharp.Formats.Png;
using RasterSharp.Formats.WebP;

namespace RasterSharp.Formats;

/// <summary>The single place where image formats are wired to their detector, decoder and encoder.</summary>
internal static class CodecRegistry
{
    private sealed record Codec(IImageFormatDetector Detector, IImageDecoder? Decoder, IImageEncoder? Encoder)
    {
        public ImageFormat Format => Detector.Format;
    }

    private static readonly Codec[] Codecs =
    [
        new(WebPFormatDetector.Instance, WebPDecoder.Instance, WebPEncoder.Instance),
        new(PngFormatDetector.Instance, PngDecoder.Instance, PngEncoder.Instance),
        new(JpegFormatDetector.Instance, JpegDecoder.Instance, JpegEncoder.Instance),
        new(BmpFormatDetector.Instance, BmpDecoder.Instance, BmpEncoder.Instance),
    ];

    private static readonly string UnrecognizedFormatMessage =
        $"The data is not a recognized image format (expected {string.Join(", ", Codecs.Select(c => c.Format.Name))}).";

    /// <summary>Number of bytes needed to recognize every registered format.</summary>
    public static int HeaderSize { get; } = Codecs.Max(c => c.Detector.HeaderSize);

    public static ImageFormat? Detect(ReadOnlySpan<byte> header)
    {
        foreach (Codec codec in Codecs)
        {
            if (codec.Detector.IsSupported(header))
            {
                return codec.Format;
            }
        }

        return null;
    }

    public static IImageFormatDetector? GetDetector(ImageFormat format) => Find(format)?.Detector;

    public static IImageDecoder? GetDecoder(ImageFormat format) => Find(format)?.Decoder;

    public static IImageEncoder? GetEncoder(ImageFormat format) => Find(format)?.Encoder;

    public static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IImageDecoder decoder = GetDecoderFor(data.Span);
        Image image;
        try
        {
            image = decoder.Decode(data, options);
        }
        catch (Exception ex) when (IsCorruptDataException(ex))
        {
            throw CorruptData(decoder.Format, ex);
        }

        if (options.ApplyExifOrientation)
        {
            image.Mutate(static x => x.AutoOrient());
        }

        return image;
    }

    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IImageDecoder decoder = GetDecoderFor(data.Span);
        try
        {
            return decoder.Identify(data, options);
        }
        catch (Exception ex) when (IsCorruptDataException(ex))
        {
            throw CorruptData(decoder.Format, ex);
        }
    }

    public static void Encode(Image image, Stream output, EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IImageEncoder encoder = GetEncoder(options.Format)
            ?? throw new NotSupportedException($"Saving images in {options.Format.Name} format is not supported.");
        encoder.Encode(image, output, options);
    }

    private static Codec? Find(ImageFormat format) => Array.Find(Codecs, c => c.Format == format);

    private static IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data)
    {
        ImageFormat format = Detect(data) ?? throw new ImageFormatException(UnrecognizedFormatMessage);
        return GetDecoder(format)
            ?? throw new NotSupportedException($"Decoding {format.Name} images is not supported yet.");
    }

    // Safety net: decoders validate their input explicitly, but any unexpected exception caused by
    // malformed data is reported as ImageFormatException rather than leaking implementation details.
    private static bool IsCorruptDataException(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException
            or OverflowException or ArithmeticException or InvalidCastException;

    private static ImageFormatException CorruptData(ImageFormat format, Exception inner) =>
        new(format, "The image data is corrupt.", inner);
}
