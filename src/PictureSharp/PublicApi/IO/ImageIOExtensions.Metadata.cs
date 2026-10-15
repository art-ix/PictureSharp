using System.Runtime.CompilerServices;
using PictureSharp.Application.Metadata;
using PictureSharp.Composition;

namespace PictureSharp;

public static partial class ImageIOExtensions
{
    extension(Image)
    {
        /// <summary>Reads JPEG, PNG/APNG or WebP file metadata without decoding pixels. Decoder limits apply.</summary>
        public static ImageMetadata ReadMetadata(string path, DecoderOptions? options = null) =>
            MetadataFor(options).Read(path, options, CancellationToken.None);

        /// <summary>Reads file metadata without decoding pixels; <paramref name="cancellationToken"/> stops I/O and parsing.</summary>
        public static ImageMetadata ReadMetadata(string path, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Read(path, options, cancellationToken);

        /// <summary>Reads metadata from the current stream position to its end, leaving the stream open. No pixels are decoded.</summary>
        public static ImageMetadata ReadMetadata(Stream stream, DecoderOptions? options = null) =>
            MetadataFor(options).Read(stream, options, CancellationToken.None);

        /// <summary>Reads stream metadata, leaving the stream open; <paramref name="cancellationToken"/> stops I/O and parsing.</summary>
        public static ImageMetadata ReadMetadata(Stream stream, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Read(stream, options, cancellationToken);

        /// <summary>Reads metadata from encoded data without copying the buffer. Byte arrays bind to this overload.</summary>
        [OverloadResolutionPriority(1)]
        public static ImageMetadata ReadMetadata(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
            MetadataFor(options).Read(data, options, CancellationToken.None);

        /// <summary>Reads metadata from encoded data without copying the buffer; <paramref name="cancellationToken"/> stops parsing.</summary>
        [OverloadResolutionPriority(1)]
        public static ImageMetadata ReadMetadata(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Read(data, options, cancellationToken);

        /// <summary>Reads metadata from a span; the encoded input is copied after checking its byte limit.</summary>
        public static ImageMetadata ReadMetadata(ReadOnlySpan<byte> data, DecoderOptions? options = null) =>
            MetadataFor(options).Read(data, options, CancellationToken.None);

        /// <summary>Reads metadata from a span (the input is copied); <paramref name="cancellationToken"/> stops parsing.</summary>
        public static ImageMetadata ReadMetadata(ReadOnlySpan<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Read(data, options, cancellationToken);

        /// <summary>Reads file metadata asynchronously without decoding pixels.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default) =>
            MetadataFor(null).ReadAsync(path, null, cancellationToken);

        /// <summary>Reads file metadata asynchronously without decoding pixels. Decoder limits apply.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            MetadataFor(options).ReadAsync(path, options, cancellationToken);

        /// <summary>Reads stream metadata asynchronously to the end, leaving the stream open. No pixels are decoded.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(Stream stream, CancellationToken cancellationToken = default) =>
            MetadataFor(null).ReadAsync(stream, null, cancellationToken);

        /// <summary>Reads stream metadata asynchronously to the end, leaving the stream open. Decoder limits apply.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            MetadataFor(options).ReadAsync(stream, options, cancellationToken);

        /// <summary>
        /// Returns a new JPEG, PNG/APNG or WebP buffer with edited metadata; <paramref name="data"/> is not modified.
        /// Compressed image data is copied byte for byte, never recompressed. Byte arrays bind to this overload.
        /// </summary>
        [OverloadResolutionPriority(1)]
        public static byte[] EditMetadata(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options = null) =>
            MetadataFor(options).Edit(data, edit, options, CancellationToken.None);

        /// <summary>Returns a new buffer with edited metadata; <paramref name="data"/> is not modified and image data is never recompressed.</summary>
        [OverloadResolutionPriority(1)]
        public static byte[] EditMetadata(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Edit(data, edit, options, cancellationToken);

        /// <summary>Returns a new buffer with edited metadata. The span is copied after checking its byte limit; image data is never recompressed.</summary>
        public static byte[] EditMetadata(ReadOnlySpan<byte> data, Action<ImageMetadata> edit, DecoderOptions? options = null) =>
            MetadataFor(options).Edit(data, edit, options, CancellationToken.None);

        /// <summary>Returns a new buffer with edited metadata (the span is copied); image data is never recompressed.</summary>
        public static byte[] EditMetadata(ReadOnlySpan<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Edit(data, edit, options, cancellationToken);

        /// <summary>
        /// Edits file metadata without recompression. The destination is replaced atomically after success; source and
        /// destination may be the same path. An edit that changes nothing leaves an in-place file untouched.
        /// </summary>
        public static void EditMetadata(string sourcePath, string destinationPath, Action<ImageMetadata> edit, DecoderOptions? options = null) =>
            MetadataFor(options).Edit(sourcePath, destinationPath, edit, options, CancellationToken.None);

        /// <summary>Edits file metadata without recompression and atomically replaces the destination after success.</summary>
        public static void EditMetadata(string sourcePath, string destinationPath, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Edit(sourcePath, destinationPath, edit, options, cancellationToken);

        /// <summary>
        /// Edits stream metadata without recompression, leaving both streams open. Stream output is not atomic: an I/O
        /// error while writing can leave partial output. Use the path overload for atomic replacement.
        /// </summary>
        public static void EditMetadata(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options = null) =>
            MetadataFor(options).Edit(source, destination, edit, options, CancellationToken.None);

        /// <summary>Edits stream metadata without recompression. Stream output is not atomic; cancellation while writing can leave partial output.</summary>
        public static void EditMetadata(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken) =>
            MetadataFor(options).Edit(source, destination, edit, options, cancellationToken);

        /// <summary>Edits file metadata asynchronously without recompression and atomically replaces the destination after success.</summary>
        public static Task EditMetadataAsync(string sourcePath, string destinationPath, Action<ImageMetadata> edit, CancellationToken cancellationToken = default) =>
            MetadataFor(null).EditAsync(sourcePath, destinationPath, edit, null, cancellationToken);

        /// <summary>Edits file metadata asynchronously without recompression. Decoder limits apply; the destination is replaced atomically.</summary>
        public static Task EditMetadataAsync(string sourcePath, string destinationPath, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            MetadataFor(options).EditAsync(sourcePath, destinationPath, edit, options, cancellationToken);

        /// <summary>Edits stream metadata asynchronously without recompression, leaving streams open. Stream output is not atomic.</summary>
        public static Task EditMetadataAsync(Stream source, Stream destination, Action<ImageMetadata> edit, CancellationToken cancellationToken = default) =>
            MetadataFor(null).EditAsync(source, destination, edit, null, cancellationToken);

        /// <summary>Edits stream metadata asynchronously without recompression. Decoder limits apply; stream output is not atomic.</summary>
        public static Task EditMetadataAsync(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            MetadataFor(options).EditAsync(source, destination, edit, options, cancellationToken);
    }

    private static ImageMetadataService MetadataFor(DecoderOptions? options) =>
        (options?.Configuration ?? LibraryServices.Configuration).Metadata;
}
