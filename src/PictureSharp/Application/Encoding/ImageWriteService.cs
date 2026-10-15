using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Identification;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Application.Encoding;

internal sealed class ImageWriteService(IImageOutput output, ImageEncodeService encoder, ICodecResolver codecs)
{
    /// <summary>
    /// Saves the image in the format given by the file extension, with that format's default options
    /// (.webp: lossy quality 75; .png; .jpg: quality 75; .bmp; .gif; .pcx; .ico: PNG).
    /// </summary>
    public void Save(Image image, string path) => Save(image, path, DefaultOptions(FormatFromPath(path)));

    /// <summary>Saves the image with the given encoder options; the options decide the format.</summary>
    public void Save(Image image, string path, EncoderOptions options) => Save(image, path, options, CancellationToken.None);

    /// <summary>
    /// Saves the image with the given encoder options; <paramref name="cancellationToken"/> stops the encoding work.
    /// The file is written only after encoding has finished, so a canceled save leaves no file behind.
    /// </summary>
    public void Save(Image image, string path, EncoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        output.WriteFile(path, Encode(image, options, cancellationToken));
    }

    /// <summary>Writes the image to a stream in the given format with default options. The stream is left open.</summary>
    public void Save(Image image, Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Save(image, stream, DefaultOptions(format));
    }

    /// <summary>Writes the image to a stream with the given encoder options. The stream is left open and need not be seekable.</summary>
    public void Save(Image image, Stream stream, EncoderOptions options) => Save(image, stream, options, CancellationToken.None);

    /// <summary>
    /// Writes the image to a stream with the given encoder options; <paramref name="cancellationToken"/> stops the
    /// encoding work. Encoders write as they go, so a canceled save may leave partial output in the stream.
    /// </summary>
    public void Save(Image image, Stream stream, EncoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(image);
        image.ThrowIfDisposed();
        encoder.Encode(image, stream, options, cancellationToken);
    }

    /// <summary>Encodes the image in the format given by the file extension and writes it asynchronously.</summary>
    public Task SaveAsync(Image image, string path, CancellationToken cancellationToken = default) =>
        SaveAsync(image, path, DefaultOptions(FormatFromPath(path)), cancellationToken);

    /// <summary>Encodes the image and writes it to a file asynchronously.</summary>
    public async Task SaveAsync(Image image, string path, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = Encode(image, options, cancellationToken);
        await output.WriteFileAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the image in the given format with default options and writes it to a stream asynchronously.</summary>
    public Task SaveAsync(Image image, Stream stream, ImageFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        return SaveAsync(image, stream, DefaultOptions(format), cancellationToken);
    }

    /// <summary>Encodes the image and writes it to a stream asynchronously. The stream is left open.</summary>
    public async Task SaveAsync(Image image, Stream stream, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = Encode(image, options, cancellationToken);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the image into a new byte array using the format's default options.</summary>
    public byte[] Encode(Image image, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Encode(image, DefaultOptions(format));
    }

    /// <summary>Encodes the image into a new byte array; the options decide the format.</summary>
    public byte[] Encode(Image image, EncoderOptions options) => Encode(image, options, CancellationToken.None);

    /// <summary>Encodes the image into a new byte array; <paramref name="cancellationToken"/> stops the encoding work.</summary>
    public byte[] Encode(Image image, EncoderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var stream = new PooledBufferStream();
        Save(image, stream, options, cancellationToken);
        return stream.ToArray();
    }

    private EncoderOptions DefaultOptions(ImageFormat format) =>
        codecs.GetEncoder(format)?.CreateDefaultOptions()
        ?? throw new NotSupportedException($"Saving images in {format.Name} format is not supported.");

    private ImageFormat FormatFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return FormatLookup.FromExtension(path, codecs.Formats)
            ?? throw new NotSupportedException($"Cannot determine the image format from the extension of '{path}'.");
    }
}
