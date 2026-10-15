namespace PictureSharp.Formats;

/// <summary>Encodes images into one format for an <see cref="IImageFormatModule"/>. Implementations must be thread-safe.</summary>
public interface IImageFormatEncoder
{
    /// <summary>The options used when a save does not give any, e.g. <c>image.Save("picture.tga")</c>.</summary>
    EncoderOptions CreateDefaultOptions();

    /// <summary>
    /// Writes <paramref name="image"/> to <paramref name="output"/>; the image is borrowed and the stream stays open.
    /// Long work should observe <paramref name="cancellationToken"/>.
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
