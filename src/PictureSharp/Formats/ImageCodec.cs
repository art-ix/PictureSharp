namespace PictureSharp.Formats;

/// <summary>Runs registered codecs and normalizes errors caused by malformed input.</summary>
internal static class ImageCodec
{
    public static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Internal.StreamBuffer.CheckLength(data.Length, options.MaxInputBytes);
        IImageDecoder decoder = CodecRegistry.GetDecoderFor(data.Span);
        Image image;
        try
        {
            image = decoder.Decode(data, options, cancellationToken);
        }
        catch (Exception ex) when (IsCorruptDataException(ex))
        {
            throw CorruptData(decoder.Format, ex);
        }
        catch (AggregateException ex) when (Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (options.ApplyExifOrientation)
        {
            try
            {
                image.Mutate(static x => x.AutoOrient(), cancellationToken);
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }

        return image;
    }

    public static ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        Internal.StreamBuffer.CheckLength(data.Length, options.MaxInputBytes);
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

    public static void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        IImageEncoder encoder = CodecRegistry.GetEncoder(options.Format)
            ?? throw new NotSupportedException($"Saving images in {options.Format.Name} format is not supported.");
        try
        {
            encoder.Encode(image, output, options, cancellationToken);
        }
        catch (AggregateException ex) when (Canceled(ex, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    // Safety net: decoders validate their input explicitly, but any unexpected exception caused by
    // malformed data is reported as ImageFormatException rather than leaking implementation details.
    internal static bool IsCorruptDataException(Exception ex) =>
        ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException
            or OverflowException or ArithmeticException or InvalidCastException;

    internal static ImageFormatException CorruptData(ImageFormat format, Exception inner) =>
        new(format, "The image data is corrupt.", inner);

    // A parallel loop wraps an OperationCanceledException thrown by its body; report the cancellation itself.
    internal static bool Canceled(AggregateException ex, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            && ex.Flatten().InnerExceptions.All(static inner => inner is OperationCanceledException);
}
