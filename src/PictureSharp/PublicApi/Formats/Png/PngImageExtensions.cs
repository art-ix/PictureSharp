
namespace PictureSharp.Formats.Png;

/// <summary>PNG shortcuts for <see cref="Image"/>.</summary>
public static class PngImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as PNG; null options mean the defaults. Animations are written as APNG unless <see cref="PngEncoderOptions.SaveAnimation"/> is off.</summary>
        public void SaveAsPng(string path, PngEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as PNG; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsPng(Stream stream, PngEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as PNG asynchronously; null options mean the defaults.</summary>
        public Task SaveAsPngAsync(string path, PngEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as PNG asynchronously; null options mean the defaults.</summary>
        public Task SaveAsPngAsync(Stream stream, PngEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as PNG; null options mean the defaults.</summary>
        public byte[] ToPng(PngEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
