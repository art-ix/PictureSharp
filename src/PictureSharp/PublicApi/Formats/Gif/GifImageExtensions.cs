
namespace PictureSharp.Formats.Gif;

/// <summary>GIF shortcuts for <see cref="Image"/>.</summary>
public static class GifImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as GIF; null options mean the defaults. Animations keep all frames unless <see cref="GifEncoderOptions.SaveAnimation"/> is off.</summary>
        public void SaveAsGif(string path, GifEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as GIF; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsGif(Stream stream, GifEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as GIF asynchronously; null options mean the defaults.</summary>
        public Task SaveAsGifAsync(string path, GifEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as GIF asynchronously; null options mean the defaults.</summary>
        public Task SaveAsGifAsync(Stream stream, GifEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as GIF; null options mean the defaults.</summary>
        public byte[] ToGif(GifEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
