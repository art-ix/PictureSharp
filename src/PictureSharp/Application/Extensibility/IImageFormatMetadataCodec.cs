namespace PictureSharp.Formats;

/// <summary>
/// Reads, and optionally rewrites, a format's ICC, EXIF and XMP metadata without decoding or encoding pixels, for
/// <c>Image.ReadMetadata</c> and <c>Image.EditMetadata</c>. Supplied by <see cref="IImageFormatModule.MetadataCodec"/>;
/// implementations must be thread-safe and must not keep the input or context after returning.
/// </summary>
/// <remarks>
/// The library checks the input and metadata limits, cancellation and the edit itself, writes files atomically and
/// reports corrupt data as <see cref="ImageFormatException"/> as for <see cref="IImageFormatDecoder"/>.
/// </remarks>
public interface IImageFormatMetadataCodec
{
    /// <summary>
    /// Reads the metadata blocks of <paramref name="data"/>. Charge their decompressed size with
    /// <see cref="ImageDecodeContext.ReserveMetadata"/> before copying them.
    /// </summary>
    ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context);

    /// <summary>
    /// Returns a copy of <paramref name="data"/> that stores <paramref name="metadata"/> instead of
    /// <paramref name="original"/>, the result of <see cref="Read"/> for the same data. Called only when they differ.
    /// The default implementation throws <see cref="NotSupportedException"/>: the format supports reading only.
    /// </summary>
    byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context) =>
        throw new NotSupportedException("Editing metadata is not supported for this format.");
}
