using System.Runtime.CompilerServices;
using PictureSharp.Composition;

namespace PictureSharp;

public static partial class ImageIOExtensions
{
    extension(Image)
    {
        /// <summary>Reads JPEG, PNG/APNG or WebP metadata without decoding pixels. Limits and cancellation apply.</summary>
        public static ImageMetadata ReadMetadata(string path, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Read(path, options, cancellationToken);

        /// <summary>Reads metadata from the current stream position to its end, leaving it open. No pixels are decoded.</summary>
        public static ImageMetadata ReadMetadata(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Read(stream, options, cancellationToken);

        /// <summary>Reads metadata from encoded JPEG, PNG/APNG or WebP data without decoding pixels.</summary>
        [OverloadResolutionPriority(1)]
        public static ImageMetadata ReadMetadata(ReadOnlyMemory<byte> data, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Read(data, options, cancellationToken);

        /// <summary>Reads metadata from a span, copying the encoded input after checking its byte limit.</summary>
        public static ImageMetadata ReadMetadata(ReadOnlySpan<byte> data, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Read(data, options, cancellationToken);

        /// <summary>Edits metadata from a span, copying the encoded input after checking its byte limit. Image data is never recompressed.</summary>
        public static byte[] EditMetadata(ReadOnlySpan<byte> data, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Edit(data, edit, options, cancellationToken);

        /// <summary>Reads file metadata asynchronously without decoding pixels.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(string path, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.ReadAsync(path, options, cancellationToken);

        /// <summary>Reads stream metadata asynchronously, leaving the stream open. No pixels are decoded.</summary>
        public static Task<ImageMetadata> ReadMetadataAsync(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.ReadAsync(stream, options, cancellationToken);

        /// <summary>Edits JPEG, PNG/APNG or WebP metadata, copying compressed image data unchanged into a new result.</summary>
        [OverloadResolutionPriority(1)]
        public static byte[] EditMetadata(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Edit(data, edit, options, cancellationToken);

        /// <summary>Edits metadata without recompression. Atomically replaces the destination after success; source and destination may be the same path.</summary>
        public static void EditMetadata(string sourcePath, string destinationPath, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Edit(sourcePath, destinationPath, edit, options, cancellationToken);

        /// <summary>Edits metadata without recompression, leaving both streams open. Output may be partial on an I/O error or cancellation while writing.</summary>
        public static void EditMetadata(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.Edit(source, destination, edit, options, cancellationToken);

        /// <summary>Edits file metadata asynchronously without recompression and atomically replaces the destination after success.</summary>
        public static Task EditMetadataAsync(string sourcePath, string destinationPath, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.EditAsync(sourcePath, destinationPath, edit, options, cancellationToken);

        /// <summary>Edits stream metadata asynchronously without recompression, leaving streams open. Output may be partial on a write failure.</summary>
        public static Task EditMetadataAsync(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options = null, CancellationToken cancellationToken = default) =>
            (options?.Configuration ?? LibraryServices.Configuration).Metadata.EditAsync(source, destination, edit, options, cancellationToken);
    }
}
