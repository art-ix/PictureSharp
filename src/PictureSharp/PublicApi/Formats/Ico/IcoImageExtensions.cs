using PictureSharp.IO;

namespace PictureSharp.Formats.Ico;

/// <summary>ICO shortcuts for <see cref="Image"/>.</summary>
public static class IcoImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as ICO; null options mean the defaults. Only the first frame is written.</summary>
        public void SaveAsIco(string path, IcoEncoderOptions? options = null) => ImageWriter.Save(image, path, options ?? new());

        /// <summary>Writes the image to a stream as ICO; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsIco(Stream stream, IcoEncoderOptions? options = null) => ImageWriter.Save(image, stream, options ?? new());

        /// <summary>Saves the image as ICO asynchronously; null options mean the defaults.</summary>
        public Task SaveAsIcoAsync(string path, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            ImageWriter.SaveAsync(image, path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as ICO asynchronously; null options mean the defaults.</summary>
        public Task SaveAsIcoAsync(Stream stream, IcoEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            ImageWriter.SaveAsync(image, stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as ICO; null options mean the defaults.</summary>
        public byte[] ToIco(IcoEncoderOptions? options = null) => ImageWriter.Encode(image, options ?? new());
    }
}
