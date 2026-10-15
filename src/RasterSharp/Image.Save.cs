using RasterSharp.Formats;
using RasterSharp.Internal;

namespace RasterSharp;

public sealed partial class Image
{
    /// <summary>
    /// Saves the image in the format given by the file extension, with that format's default options
    /// (.webp: lossy quality 75; .png; .jpg: quality 75; .bmp; .gif; .pcx; .ico: PNG).
    /// </summary>
    public void Save(string path) => Save(path, FormatFromPath(path).CreateDefaultEncoderOptions());

    /// <summary>Saves the image with the given encoder options; the options decide the format.</summary>
    public void Save(string path, EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, Encode(options));
    }

    /// <summary>Writes the image to a stream in the given format with default options. The stream is left open.</summary>
    public void Save(Stream stream, ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        Save(stream, format.CreateDefaultEncoderOptions());
    }

    /// <summary>Writes the image to a stream with the given encoder options. The stream is left open and need not be seekable.</summary>
    public void Save(Stream stream, EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        ImageCodec.Encode(this, stream, options);
    }

    /// <summary>Encodes the image in the format given by the file extension and writes it asynchronously.</summary>
    public Task SaveAsync(string path, CancellationToken cancellationToken = default) =>
        SaveAsync(path, FormatFromPath(path).CreateDefaultEncoderOptions(), cancellationToken);

    /// <summary>Encodes the image and writes it to a file asynchronously.</summary>
    public Task SaveAsync(string path, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return File.WriteAllBytesAsync(path, Encode(options), cancellationToken);
    }

    /// <summary>Encodes the image in the given format with default options and writes it to a stream asynchronously.</summary>
    public Task SaveAsync(Stream stream, ImageFormat format, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        return SaveAsync(stream, format.CreateDefaultEncoderOptions(), cancellationToken);
    }

    /// <summary>Encodes the image and writes it to a stream asynchronously. The stream is left open.</summary>
    public async Task SaveAsync(Stream stream, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = Encode(options);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the image into a new byte array using the format's default options.</summary>
    public byte[] Encode(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Encode(format.CreateDefaultEncoderOptions());
    }

    /// <summary>Encodes the image into a new byte array; the options decide the format.</summary>
    public byte[] Encode(EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var stream = new PooledBufferStream();
        Save(stream, options);
        return stream.ToArray();
    }

    private static ImageFormat FormatFromPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ImageFormat.FromExtension(path)
            ?? throw new NotSupportedException($"Cannot determine the image format from the extension of '{path}'.");
    }
}
