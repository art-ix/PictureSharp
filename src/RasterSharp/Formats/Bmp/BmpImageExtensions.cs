namespace RasterSharp;

/// <summary>BMP shortcuts for <see cref="Image"/>.</summary>
public static class BmpImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as BMP; null options mean the defaults. Only the first frame is written.</summary>
        public void SaveAsBmp(string path, BmpEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as BMP; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsBmp(Stream stream, BmpEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as BMP asynchronously; null options mean the defaults.</summary>
        public Task SaveAsBmpAsync(string path, BmpEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as BMP asynchronously; null options mean the defaults.</summary>
        public Task SaveAsBmpAsync(Stream stream, BmpEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as BMP; null options mean the defaults.</summary>
        public byte[] ToBmp(BmpEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
