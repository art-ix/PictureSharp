using System.Drawing;
using PictureSharp.Processing;

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

        try
        {
            ApplyTargetSize(image, options, cancellationToken);
            if (options.ApplyExifOrientation)
            {
                image.Mutate(static x => x.AutoOrient(), cancellationToken);
            }
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }

    /// <summary>
    /// The size <see cref="DecoderOptions.TargetSize"/> gives a <paramref name="width"/>×<paramref name="height"/> image
    /// stored with <paramref name="orientation"/>, in stored orientation; null when the image keeps its size.
    /// </summary>
    internal static Size? TargetSize(int width, int height, Size target, ExifOrientation orientation)
    {
        // The box applies to the oriented image; orientations 5-8 swap the axes.
        bool swap = orientation >= ExifOrientation.LeftTop;
        (int w, int h) = swap ? (height, width) : (width, height);
        if (Resizer.ThumbnailSize(w, h, target.Width, target.Height) is not var (tw, th))
        {
            return null;
        }

        return swap ? new Size(th, tw) : new Size(tw, th);
    }

    // Shrinks to DecoderOptions.TargetSize before the orientation is applied; a reduced JPEG decode is resampled
    // from the area its rounded-up raster actually covers, like the box Pillow's draft returns.
    private static void ApplyTargetSize(Image image, DecoderOptions options, CancellationToken cancellationToken)
    {
        (Size stored, int scale) = image.ReducedDecode ?? (image.Size, 1);
        image.ReducedDecode = null;
        if (options.TargetSize is not { } target)
        {
            return;
        }

        ExifOrientation orientation = options.ApplyExifOrientation ? image.Metadata.Orientation : ExifOrientation.Unknown;
        if (TargetSize(stored.Width, stored.Height, target, orientation) is not { } size)
        {
            return;
        }

        if (scale == 1)
        {
            var resize = new ResizeOptions { Size = size, Filter = options.TargetResampling };
            image.Mutate(x => x.Resize(resize), cancellationToken);
            return;
        }

        ImageFrame frame = image.Frames[0];
        Rgba32[] pixels = Resizer.Resize(frame.Pixels, frame.Width, frame.Height, size.Width, size.Height, options.TargetResampling,
            0, 0, (float)stored.Width / scale, (float)stored.Height / scale, cancellationToken);
        frame.Replace(size.Width, size.Height, pixels);
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
