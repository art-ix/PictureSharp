using PictureSharp.Composition;

namespace PictureSharp.IO;

/// <summary>Encodes images and writes them to files or streams.</summary>
public static partial class ImageWriter
{
    /// <summary>
    /// Saves the image in the format given by the file extension, with that format's default options
    /// (.webp: lossy quality 75; .png; .jpg: quality 75; .bmp; .gif; .pcx; .ico: PNG).
    /// </summary>
    public static void Save(Image image, string path) =>
        LibraryServices.Writer.Save(image, path);

    /// <summary>Saves the image with the given encoder options; the options decide the format.</summary>
    public static void Save(Image image, string path, EncoderOptions options) =>
        LibraryServices.Writer.Save(image, path, options);

    /// <summary>
    /// Saves the image with the given encoder options; <paramref name="cancellationToken"/> stops the encoding work.
    /// The file is written only after encoding has finished, so a canceled save leaves no file behind.
    /// </summary>
    public static void Save(Image image, string path, EncoderOptions options, CancellationToken cancellationToken) =>
        LibraryServices.Writer.Save(image, path, options, cancellationToken);

    /// <summary>Writes the image to a stream in the given format with default options. The stream is left open.</summary>
    public static void Save(Image image, Stream stream, ImageFormat format) =>
        LibraryServices.Writer.Save(image, stream, format);

    /// <summary>Writes the image to a stream with the given encoder options. The stream is left open and need not be seekable.</summary>
    public static void Save(Image image, Stream stream, EncoderOptions options) =>
        LibraryServices.Writer.Save(image, stream, options);

    /// <summary>
    /// Writes the image to a stream with the given encoder options; <paramref name="cancellationToken"/> stops the
    /// encoding work. Encoders write as they go, so a canceled save may leave partial output in the stream.
    /// </summary>
    public static void Save(Image image, Stream stream, EncoderOptions options, CancellationToken cancellationToken) =>
        LibraryServices.Writer.Save(image, stream, options, cancellationToken);

    /// <summary>Encodes the image in the format given by the file extension and writes it asynchronously.</summary>
    public static Task SaveAsync(Image image, string path, CancellationToken cancellationToken = default) =>
        LibraryServices.Writer.SaveAsync(image, path, cancellationToken);

    /// <summary>Encodes the image and writes it to a file asynchronously.</summary>
    public static Task SaveAsync(Image image, string path, EncoderOptions options, CancellationToken cancellationToken = default) =>
        LibraryServices.Writer.SaveAsync(image, path, options, cancellationToken);

    /// <summary>Encodes the image in the given format with default options and writes it to a stream asynchronously.</summary>
    public static Task SaveAsync(Image image, Stream stream, ImageFormat format, CancellationToken cancellationToken = default) =>
        LibraryServices.Writer.SaveAsync(image, stream, format, cancellationToken);

    /// <summary>Encodes the image and writes it to a stream asynchronously. The stream is left open.</summary>
    public static Task SaveAsync(Image image, Stream stream, EncoderOptions options, CancellationToken cancellationToken = default) =>
        LibraryServices.Writer.SaveAsync(image, stream, options, cancellationToken);

    /// <summary>Encodes the image into a new byte array using the format's default options.</summary>
    public static byte[] Encode(Image image, ImageFormat format) =>
        LibraryServices.Writer.Encode(image, format);

    /// <summary>Encodes the image into a new byte array; the options decide the format.</summary>
    public static byte[] Encode(Image image, EncoderOptions options) =>
        LibraryServices.Writer.Encode(image, options);

    /// <summary>Encodes the image into a new byte array; <paramref name="cancellationToken"/> stops the encoding work.</summary>
    public static byte[] Encode(Image image, EncoderOptions options, CancellationToken cancellationToken) =>
        LibraryServices.Writer.Encode(image, options, cancellationToken);
}
