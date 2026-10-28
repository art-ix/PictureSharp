
namespace PictureSharp.Application.Abstractions;

/// <summary>Encodes an image into one format.</summary>
internal interface IImageEncoder
{
    ImageFormat Format { get; }

    EncoderOptions CreateDefaultOptions();

    /// <summary>Whether <see cref="Encode"/> stores images of <paramref name="pixelType"/>; others receive an 8-bit copy.</summary>
    bool SupportsPixelType(PixelType pixelType) => pixelType == PixelType.Rgba32;

    void Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken);
}

/// <summary>An encoder with its own options type; the untyped members are implemented once here.</summary>
internal interface IImageEncoder<TOptions> : IImageEncoder
    where TOptions : EncoderOptions, new()
{
    void Encode(Image image, Stream output, TOptions options, CancellationToken cancellationToken);

    EncoderOptions IImageEncoder.CreateDefaultOptions() => new TOptions();

    void IImageEncoder.Encode(Image image, Stream output, EncoderOptions options, CancellationToken cancellationToken) =>
        Encode(image, output, options as TOptions
            ?? throw new ArgumentException($"Expected {typeof(TOptions).Name}.", nameof(options)), cancellationToken);
}
