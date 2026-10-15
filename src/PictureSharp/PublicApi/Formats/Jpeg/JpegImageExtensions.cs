
namespace PictureSharp.Formats.Jpeg;

/// <summary>JPEG shortcuts for <see cref="Image"/>.</summary>
public static class JpegImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as JPEG; null options mean the defaults. Only the first frame is written; transparency is composited on <see cref="JpegEncoderOptions.BackgroundColor"/>.</summary>
        public void SaveAsJpeg(string path, JpegEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as JPEG; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsJpeg(Stream stream, JpegEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as JPEG asynchronously; null options mean the defaults.</summary>
        public Task SaveAsJpegAsync(string path, JpegEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as JPEG asynchronously; null options mean the defaults.</summary>
        public Task SaveAsJpegAsync(Stream stream, JpegEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as JPEG; null options mean the defaults.</summary>
        public byte[] ToJpeg(JpegEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
