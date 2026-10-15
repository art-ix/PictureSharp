using System.Runtime.CompilerServices;
using PictureSharp.Composition;

namespace PictureSharp;

/// <summary>Loads, identifies and saves encoded images through <see cref="Image"/>.</summary>
public static partial class ImageIOExtensions
{
    extension(Image)
    {
        /// <summary>Decodes an image file (WebP, PNG, JPEG, BMP, GIF, PCX or ICO; the format is detected from content).</summary>
        public static Image Load(string path, DecoderOptions? options = null) =>
            LibraryServices.ReaderFor(options).Load(path, options);

        /// <summary>Decodes an image file; <paramref name="cancellationToken"/> also stops the decoding work.</summary>
        public static Image Load(string path, DecoderOptions? options, CancellationToken cancellationToken) =>
            LibraryServices.ReaderFor(options).Load(path, options, cancellationToken);

        /// <summary>Decodes an image from a stream, reading it to the end. The stream need not be seekable.</summary>
        public static Image Load(Stream stream, DecoderOptions? options = null) =>
            LibraryServices.ReaderFor(options).Load(stream, options);

        /// <summary>
        /// Decodes an image from a stream, reading it to the end; <paramref name="cancellationToken"/> also stops the
        /// decoding work. The stream need not be seekable.
        /// </summary>
        public static Image Load(Stream stream, DecoderOptions? options, CancellationToken cancellationToken) =>
            LibraryServices.ReaderFor(options).Load(stream, options, cancellationToken);

        /// <summary>Decodes an image from memory (the data is copied).</summary>
        public static Image Load(ReadOnlySpan<byte> data, DecoderOptions? options = null) =>
            LibraryServices.ReaderFor(options).Load(data, options);

        /// <summary>Decodes an image from memory (the data is copied); <paramref name="cancellationToken"/> stops the decoding work.</summary>
        public static Image Load(ReadOnlySpan<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
            LibraryServices.ReaderFor(options).Load(data, options, cancellationToken);

        /// <summary>Decodes an image from memory without copying the buffer. Byte arrays bind to this overload.</summary>
        [OverloadResolutionPriority(1)]
        public static Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
            LibraryServices.ReaderFor(options).Load(data, options);

        /// <summary>
        /// Decodes an image from memory without copying the buffer; <paramref name="cancellationToken"/> stops the
        /// decoding work. Byte arrays bind to this overload.
        /// </summary>
        [OverloadResolutionPriority(1)]
        public static Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken cancellationToken) =>
            LibraryServices.ReaderFor(options).Load(data, options, cancellationToken);

        /// <summary>Reads a file asynchronously and decodes it.</summary>
        public static Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default) =>
            LibraryServices.Reader.LoadAsync(path, cancellationToken);

        /// <summary>
        /// Reads a file asynchronously and decodes it. The decoding runs synchronously on the calling thread and also
        /// observes <paramref name="cancellationToken"/>.
        /// </summary>
        public static Task<Image> LoadAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            LibraryServices.ReaderFor(options).LoadAsync(path, options, cancellationToken);

        /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
        public static Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
            LibraryServices.Reader.LoadAsync(stream, cancellationToken);

        /// <summary>
        /// Reads a stream asynchronously to the end and decodes it. The decoding also observes
        /// <paramref name="cancellationToken"/>.
        /// </summary>
        public static Task<Image> LoadAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default) =>
            LibraryServices.ReaderFor(options).LoadAsync(stream, options, cancellationToken);
    }
}
