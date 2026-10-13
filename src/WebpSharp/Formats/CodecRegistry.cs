namespace WebpSharp.Formats;

/// <summary>Maps image formats to their decoder and encoder implementations.</summary>
internal static class CodecRegistry
{
    private static readonly IImageDecoder[] Decoders = [WebP.WebPDecoder.Instance, Png.PngDecoder.Instance, Bmp.BmpDecoder.Instance];

    private static readonly IImageEncoder[] Encoders = [Png.PngEncoder.Instance, Bmp.BmpEncoder.Instance];

    public static IImageDecoder? GetDecoder(ImageFormat format) => Array.Find(Decoders, d => d.Format == format);

    public static IImageEncoder? GetEncoder(ImageFormat format) => Array.Find(Encoders, e => e.Format == format);

    public static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IImageDecoder decoder = GetDecoderFor(data.Span);
        Image image = Guard(decoder.Format, data, options, static (d, s, o) => d.Decode(s, o), decoder);
        if (options.ApplyExifOrientation)
        {
            image.AutoOrient();
        }

        return image;
    }

    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IImageDecoder decoder = GetDecoderFor(data.Span);
        return Guard(decoder.Format, data, options, static (d, s, o) => d.Identify(s, o), decoder);
    }

    public static void Encode(Image image, Stream output, EncoderOptions options)
    {
        options.Validate();
        IImageEncoder encoder = GetEncoder(options.Format)
            ?? throw new NotSupportedException($"Saving images in {options.Format.Name} format is not supported.");
        encoder.Encode(image, output, options);
    }

    private static IImageDecoder GetDecoderFor(ReadOnlySpan<byte> data)
    {
        ImageFormat format = FormatDetector.Detect(data)
            ?? throw new ImageFormatException("The data is not a recognized image format (expected WebP, PNG, JPEG or BMP).");
        return GetDecoder(format)
            ?? throw new NotSupportedException($"Decoding {format.Name} images is not supported yet.");
    }

    private delegate T DecodeFunc<T>(IImageDecoder decoder, ReadOnlyMemory<byte> data, DecoderOptions options);

    // Safety net: decoders validate their input explicitly, but any unexpected exception caused by
    // malformed data is reported as ImageFormatException rather than leaking implementation details.
    private static T Guard<T>(ImageFormat format, ReadOnlyMemory<byte> data, DecoderOptions options, DecodeFunc<T> func, IImageDecoder decoder)
    {
        try
        {
            return func(decoder, data, options);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException
                                       or OverflowException or ArithmeticException or InvalidCastException)
        {
            throw new ImageFormatException(format, "The image data is corrupt.", ex);
        }
    }
}
