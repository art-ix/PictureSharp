using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.IO;

internal sealed class ImageInput : IImageInput
{
    public byte[] ReadFile(string path, long maxBytes, CancellationToken cancellationToken = default) => StreamBuffer.ReadFile(path, maxBytes, cancellationToken);
    public byte[] ReadAll(Stream stream, long maxBytes, CancellationToken cancellationToken = default) => StreamBuffer.ReadAll(stream, maxBytes, cancellationToken);
    public Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken) => StreamBuffer.ReadFileAsync(path, maxBytes, cancellationToken);
    public Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken cancellationToken) => StreamBuffer.ReadAllAsync(stream, maxBytes, cancellationToken);

    /// <summary>Detects the format of a file from its first bytes.</summary>
    public int ReadHeader(string path, Span<byte> header)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var handle = File.OpenHandle(path);
        int read = 0;
        int n;
        while (read < header.Length && (n = RandomAccess.Read(handle, header[read..], read)) > 0)
        {
            read += n;
        }

        return read;
    }

    /// <summary>
    /// Detects the format from the next bytes of <paramref name="stream"/>. A seekable stream is rewound to where it
    /// started; a non-seekable stream loses the bytes that were read.
    /// </summary>
    public int ReadHeader(Stream stream, Span<byte> header)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long start = stream.CanSeek ? stream.Position : 0;
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        return read;
    }
}
