using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.IO;

internal sealed class ImageOutput : IImageOutput
{
    public void WriteFile(string path, byte[] data) => File.WriteAllBytes(path, data);
    public Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken) => File.WriteAllBytesAsync(path, data, cancellationToken);
}
