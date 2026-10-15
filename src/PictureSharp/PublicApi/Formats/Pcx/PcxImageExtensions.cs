
namespace PictureSharp.Formats.Pcx;

/// <summary>PCX shortcuts for <see cref="Image"/>.</summary>
public static class PcxImageExtensions
{
    extension(Image image)
    {
        /// <summary>Saves the image as PCX; null options mean the defaults. Only the first frame is written.</summary>
        public void SaveAsPcx(string path, PcxEncoderOptions? options = null) => image.Save(path, options ?? new());

        /// <summary>Writes the image to a stream as PCX; null options mean the defaults. The stream is left open.</summary>
        public void SaveAsPcx(Stream stream, PcxEncoderOptions? options = null) => image.Save(stream, options ?? new());

        /// <summary>Saves the image as PCX asynchronously; null options mean the defaults.</summary>
        public Task SaveAsPcxAsync(string path, PcxEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(path, options ?? new(), cancellationToken);

        /// <summary>Writes the image to a stream as PCX asynchronously; null options mean the defaults.</summary>
        public Task SaveAsPcxAsync(Stream stream, PcxEncoderOptions? options = null, CancellationToken cancellationToken = default) =>
            image.SaveAsync(stream, options ?? new(), cancellationToken);

        /// <summary>Encodes the image as PCX; null options mean the defaults.</summary>
        public byte[] ToPcx(PcxEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
