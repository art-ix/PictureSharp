namespace RasterSharp.Formats;

/// <summary>Runs registered codecs and normalizes errors caused by malformed input.</summary>
internal static class ImageCodec
{
    public static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        IImageDecoder decoder = CodecRegistry.GetDecoderFor(data.Span);
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
        IImageDecoder decoder = CodecRegistry.GetDecoderFor(data.Span);
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
        IImageEncoder encoder = CodecRegistry.GetEncoder(options.Format)
            ?? throw new NotSupportedException($"Saving images in {options.Format.Name} format is not supported.");
        encoder.Encode(image, output, options);
    }

    // Safety net: decoders validate their input explicitly, but any unexpected exception caused by
    // malformed data is reported as ImageFormatException rather than leaking implementation details.
    internal static bool IsCorruptDataException(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException
            or OverflowException or ArithmeticException or InvalidCastException;

    internal static ImageFormatException CorruptData(ImageFormat format, Exception inner) =>
        new(format, "The image data is corrupt.", inner);
}
