using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.IO;

internal sealed class ImageOutput : IImageOutput
{
    public void WriteFile(string path, byte[] data) => File.WriteAllBytes(path, data);
    public Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken) => File.WriteAllBytesAsync(path, data, cancellationToken);

    public Stream CreateFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, bufferSize: 64 * 1024, FileOptions.Asynchronous);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public Stream CreateTemporaryFile() =>
        new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
}
