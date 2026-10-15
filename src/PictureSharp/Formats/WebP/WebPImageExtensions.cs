namespace PictureSharp;

/// <summary>WebP shortcuts for <see cref="Image"/>.</summary>
public static class WebPImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as WebP; null options mean the defaults. Animations keep all frames.</summary>
        public void SaveAsWebP(string path, WebPEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as WebP; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsWebP(Stream stream, WebPEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as WebP asynchronously; null options mean the defaults.</summary>
        public Task SaveAsWebPAsync(string path, WebPEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as WebP asynchronously; null options mean the defaults.</summary>
        public Task SaveAsWebPAsync(Stream stream, WebPEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as WebP; null options mean the defaults.</summary>
        public byte[] ToWebP(WebPEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
