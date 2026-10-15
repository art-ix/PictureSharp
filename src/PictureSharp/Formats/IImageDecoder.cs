namespace PictureSharp.Formats;

/// <summary>Decodes one image format from an in-memory buffer.</summary>
internal interface IImageDecoder
{
    ImageFormat Format { get; }

    /// <summary>Decodes the image; long-running work checks <paramref name="cancellationToken"/> between rows, blocks or frames.</summary>
    Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken);

    ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options);
}
