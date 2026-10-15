namespace PictureSharp.Application.Abstractions;

internal interface IImageInput
{
    byte[] ReadFile(string path, long maxBytes, CancellationToken cancellationToken = default);
    byte[] ReadAll(Stream stream, long maxBytes, CancellationToken cancellationToken = default);
    Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken);
    Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken cancellationToken);
    int ReadHeader(string path, Span<byte> header);
    int ReadHeader(Stream stream, Span<byte> header);

    /// <summary>Opens a file for forward-only asynchronous reading.</summary>
    Stream OpenRead(string path);
}
