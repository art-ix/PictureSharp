namespace PictureSharp.Application.Abstractions;

internal interface IImageOutput
{
    void WriteFile(string path, byte[] data);
    Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken);
}
