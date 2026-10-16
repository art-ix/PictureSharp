using PictureSharp.Application.Hdr;
using PictureSharp.Processing;

namespace PictureSharp;

/// <summary>Reconstruction of HDR pixels from a gain map and conversion back to SDR.</summary>
public static class HdrExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// Reconstructs linear HDR pixels from the base pixels and the gain map attached by a load with
        /// <see cref="DecoderOptions.LoadGainMap"/>. The result has the image's size and a copy of its metadata.
        /// </summary>
        /// <example>
        /// <code>
        /// using Image image = Image.Load(path, new DecoderOptions { LoadGainMap = true });
        /// if (image.HasHdr)
        /// {
        ///     using HdrImage hdr = image.ToHdr();
        /// }
        /// </code>
        /// </example>
        /// <exception cref="InvalidOperationException">No gain map is attached (see <see cref="Image.Hdr"/>).</exception>
        /// <exception cref="NotSupportedException">The gain map variant is valid but not supported.</exception>
        /// <exception cref="ImageFormatException">The gain map or its metadata is corrupt.</exception>
        public HdrImage ToHdr(HdrOptions? options = null, CancellationToken cancellationToken = default) =>
            HdrService.ToHdr(image, options, cancellationToken);
    }

    extension(HdrImage hdr)
    {
        /// <summary>
        /// Maps the HDR pixels to a new 8-bit sRGB image (one frame, no gain map) with the operator described by
        /// <see cref="ToneMappingOptions"/>. The metadata is copied without the ICC profile and color description.
        /// </summary>
        public Image ToneMap(ToneMappingOptions? options = null, CancellationToken cancellationToken = default) =>
            HdrService.ToneMap(hdr, options, cancellationToken);
    }
}
