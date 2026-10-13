namespace WebpSharp.Formats;

/// <summary>Decodes one image format from an in-memory buffer.</summary>
internal interface IImageDecoder
{
    ImageFormat Format { get; }

    Image Decode(ReadOnlySpan<byte> data, DecoderOptions options);

    ImageInfo Identify(ReadOnlySpan<byte> data, DecoderOptions options);
}

/// <summary>Encodes an image into one format.</summary>
internal interface IImageEncoder
{
    ImageFormat Format { get; }

    void Encode(Image image, Stream output, EncoderOptions options);
}
