using PictureSharp.Domain.Pixels;

namespace PictureSharp.Application.Encoding;

/// <summary>
/// The format boundary for encoders that do not store an image's <see cref="PixelType"/>: they receive an 8-bit copy,
/// rounded to the nearest value without dithering. The source is borrowed and never changed.
/// </summary>
internal static class EncoderInput
{
    /// <summary>
    /// An <see cref="PixelType.Rgba32"/> copy of a 16-bit image with its frame timing, metadata and gain map; the
    /// caller disposes it.
    /// </summary>
    public static Image ToRgba32(Image image, CancellationToken cancellationToken = default)
    {
        var frames = new List<ImageFrame>(image.FrameCount);
        foreach (ImageFrame frame in image.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            frames.Add(ToRgba32(frame));
        }

        Image copy = image.CopyPropertiesTo(new Image(frames));
        copy.AttachedGainMap = image.AttachedGainMap;   // immutable; rounding keeps the base within half an 8-bit step
        return copy;
    }

    /// <summary>An unowned <see cref="PixelType.Rgba32"/> copy of a 16-bit frame, with its duration.</summary>
    public static ImageFrame ToRgba32(ImageFrame frame)
    {
        ImageFrame copy = ImageFrame.CreateUninitialized(frame.Width, frame.Height);
        PixelDepth.Narrow(frame.GetPixelSpan<Rgba64>(), copy.Pixels);
        copy.Duration = frame.Duration;
        return copy;
    }

    /// <summary>Encodes <paramref name="image"/>, converting it first when the encoder does not store its pixel type.</summary>
    public static void Encode(Abstractions.IImageEncoder encoder, Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken)
    {
        if (encoder.SupportsPixelType(image.PixelType))
        {
            encoder.Encode(image, output, options, cancellationToken);
            return;
        }

        using Image converted = ToRgba32(image, cancellationToken);
        encoder.Encode(converted, output, options, cancellationToken);
    }
}
