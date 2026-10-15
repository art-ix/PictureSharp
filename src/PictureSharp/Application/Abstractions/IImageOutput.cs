namespace PictureSharp.Application.Abstractions;

internal interface IImageOutput
{
    void WriteFile(string path, byte[] data);
    Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken);

    /// <summary>Creates (or truncates) a file for asynchronous writing.</summary>
    Stream CreateFile(string path);

    /// <summary>Deletes a file that was left incomplete; missing files are ignored.</summary>
    void DeleteFile(string path);

    /// <summary>Creates a seekable read/write temporary file that is deleted when the stream is closed.</summary>
    Stream CreateTemporaryFile();
}
