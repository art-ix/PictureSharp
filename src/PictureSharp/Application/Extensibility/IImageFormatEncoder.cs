namespace PictureSharp.Formats;

/// <summary>Encodes images into one format for an <see cref="IImageFormatModule"/>. Implementations must be thread-safe.</summary>
public interface IImageFormatEncoder
{
    /// <summary>The options used when a save does not give any, e.g. <c>image.Save("picture.tga")</c>.</summary>
    EncoderOptions CreateDefaultOptions();

    /// <summary>
    /// Whether <see cref="Encode"/> accepts images that store <paramref name="pixelType"/>. For any other type the
    /// library passes a <see cref="PixelType.Rgba32"/> copy rounded to 8 bits, so an encoder for 8-bit formats needs no
    /// code for 16-bit images. Override it to store <see cref="PixelType.Rgba64"/> samples. Default: true only for
    /// <see cref="PixelType.Rgba32"/>.
    /// </summary>
    bool SupportsPixelType(PixelType pixelType) => pixelType == PixelType.Rgba32;

    /// <summary>
    /// Writes <paramref name="image"/> to <paramref name="output"/>; the image is borrowed and the stream stays open.
    /// Long work should observe <paramref name="cancellationToken"/>. The image's <see cref="Image.PixelType"/> is one
    /// for which <see cref="SupportsPixelType"/> returns true.
    /// </summary>
    void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken);
}

/// <summary>An encoder with its own options record; the untyped members are implemented once here.</summary>
/// <typeparam name="TOptions">The options record, derived from <see cref="EncoderOptions"/>.</typeparam>
public interface IImageFormatEncoder<TOptions> : IImageFormatEncoder
    where TOptions : EncoderOptions, new()
{
    /// <summary>Writes <paramref name="image"/> to <paramref name="output"/> with typed options.</summary>
    void Encode(Image image, Stream output, TOptions options, CancellationToken cancellationToken);

    EncoderOptions IImageFormatEncoder.CreateDefaultOptions() => new TOptions();

    void IImageFormatEncoder.Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken) =>
        Encode(image, output, options as TOptions
            ?? throw new ArgumentException($"Expected {typeof(TOptions).Name}.", nameof(options)), cancellationToken);
}
