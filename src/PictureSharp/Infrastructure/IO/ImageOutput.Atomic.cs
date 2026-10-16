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
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }

    public async Task WriteAtomicFileAsync(string path, byte[] data, CancellationToken token)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".picturesharp-{Guid.NewGuid():N}.tmp");
        try
        {
            token.ThrowIfCancellationRequested();
            var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                for (int offset = 0; offset < data.Length;)
                {
                    int length = Math.Min(65536, data.Length - offset);
                    await stream.WriteAsync(data.AsMemory(offset, length), token).ConfigureAwait(false);
                    offset += length;
                }
                await stream.FlushAsync(token).ConfigureAwait(false);
                // fsync has no async API; Task.Run would only block another pool thread.
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        catch
        {
            DeleteTemporary(temporary);
            throw;
        }
    }

    // Cleanup must not replace the write's own exception; a leftover temporary file is harmless.
    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
