namespace PictureSharp.Infrastructure.IO;

internal sealed partial class ImageOutput
{
    public void WriteAtomicFile(string path, byte[] data, CancellationToken token)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".picturesharp-{Guid.NewGuid():N}.tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                for (int offset = 0; offset < data.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int length = Math.Min(65536, data.Length - offset);
                    stream.Write(data, offset, length);
                    offset += length;
                }
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    public async Task WriteAtomicFileAsync(string path, byte[] data, CancellationToken token)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".picturesharp-{Guid.NewGuid():N}.tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                for (int offset = 0; offset < data.Length;)
                {
                    int length = Math.Min(65536, data.Length - offset);
                    await stream.WriteAsync(data.AsMemory(offset, length), token).ConfigureAwait(false);
                    offset += length;
                }
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
