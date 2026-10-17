using PictureSharp.Composition;

namespace PictureSharp;

/// <summary>Loads, identifies and saves encoded images through <see cref="Image"/>.</summary>
public static partial class ImageIOExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// Saves the image in the format given by the file extension, with that format's default options
        /// (.webp: lossy quality 75; .png; .jpg: quality 75; .bmp; .gif; .pcx; .ico: PNG).
        /// </summary>
        public void Save(string path) =>
            LibraryServices.Writer.Save(image, path);

        /// <summary>
        /// Saves the image with the given encoder options; the options decide the format and, through
        /// <see cref="EncoderOptions.Configuration"/>, the configuration whose encoder writes it.
        /// </summary>
        public void Save(string path, EncoderOptions options) =>
            LibraryServices.WriterFor(options).Save(image, path, options);

        /// <summary>
        /// Saves the image with the given encoder options; <paramref name="cancellationToken"/> stops the encoding work.
        /// The file is written only after encoding has finished, so a canceled save leaves no file behind.
        /// </summary>
        public void Save(string path, EncoderOptions options, CancellationToken cancellationToken) =>
            LibraryServices.WriterFor(options).Save(image, path, options, cancellationToken);

        /// <summary>Writes the image to a stream in the given format with default options. The stream is left open.</summary>
        public void Save(Stream stream, ImageFormat format) =>
            LibraryServices.Writer.Save(image, stream, format);

        /// <summary>Writes the image to a stream with the given encoder options. The stream is left open and need not be seekable.</summary>
        public void Save(Stream stream, EncoderOptions options) =>
            LibraryServices.WriterFor(options).Save(image, stream, options);

        /// <summary>
        /// Writes the image to a stream with the given encoder options; <paramref name="cancellationToken"/> stops the
        /// encoding work. Encoders write as they go, so a canceled save may leave partial output in the stream.
        /// </summary>
        public void Save(Stream stream, EncoderOptions options, CancellationToken cancellationToken) =>
            LibraryServices.WriterFor(options).Save(image, stream, options, cancellationToken);

        /// <summary>Encodes the image in the format given by the file extension and writes it asynchronously.</summary>
        public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
            LibraryServices.Writer.SaveAsync(image, path, cancellationToken);

        /// <summary>Encodes the image and writes it to a file asynchronously.</summary>
        public Task SaveAsync(string path, EncoderOptions options, CancellationToken cancellationToken = default) =>
            LibraryServices.WriterFor(options).SaveAsync(image, path, options, cancellationToken);

        /// <summary>Encodes the image in the given format with default options and writes it to a stream asynchronously.</summary>
        public Task SaveAsync(Stream stream, ImageFormat format, CancellationToken cancellationToken = default) =>
            LibraryServices.Writer.SaveAsync(image, stream, format, cancellationToken);

        /// <summary>Encodes the image and writes it to a stream asynchronously. The stream is left open.</summary>
        public Task SaveAsync(Stream stream, EncoderOptions options, CancellationToken cancellationToken = default) =>
            LibraryServices.WriterFor(options).SaveAsync(image, stream, options, cancellationToken);

        /// <summary>Encodes the image into a new byte array using the format's default options.</summary>
        public byte[] Encode(ImageFormat format) =>
            LibraryServices.Writer.Encode(image, format);

        /// <summary>Encodes the image into a new byte array; the options decide the format.</summary>
        public byte[] Encode(EncoderOptions options) =>
            LibraryServices.WriterFor(options).Encode(image, options);

        /// <summary>Encodes the image into a new byte array; <paramref name="cancellationToken"/> stops the encoding work.</summary>
        public byte[] Encode(EncoderOptions options, CancellationToken cancellationToken) =>
            LibraryServices.WriterFor(options).Encode(image, options, cancellationToken);
    }
}
