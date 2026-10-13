namespace RasterSharp.Formats;

/// <summary>Recognizes one image format from its leading signature bytes.</summary>
internal interface IImageFormatDetector
{
    ImageFormat Format { get; }

    /// <summary>Number of leading bytes <see cref="IsSupported"/> needs.</summary>
    int HeaderSize { get; }

    bool IsSupported(ReadOnlySpan<byte> header);
}

/// <summary>Decodes one image format from an in-memory buffer.</summary>
internal interface IImageDecoder
{
    ImageFormat Format { get; }

    Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options);

    ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options);
}

/// <summary>Encodes an image into one format.</summary>
internal interface IImageEncoder
{
    ImageFormat Format { get; }

    EncoderOptions CreateDefaultOptions();

    void Encode(Image image, Stream output, EncoderOptions options);
}

/// <summary>An encoder with its own options type; the untyped members are implemented once here.</summary>
internal interface IImageEncoder<TOptions> : IImageEncoder
    where TOptions : EncoderOptions, new()
{
    void Encode(Image image, Stream output, TOptions options);

    EncoderOptions IImageEncoder.CreateDefaultOptions() => new TOptions();

    void IImageEncoder.Encode(Image image, Stream output, EncoderOptions options) =>
        Encode(image, output, options as TOptions
            ?? throw new ArgumentException($"Expected {typeof(TOptions).Name}.", nameof(options)));
}
