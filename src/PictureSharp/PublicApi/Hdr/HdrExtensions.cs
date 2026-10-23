using PictureSharp.Application.Hdr;
using PictureSharp.Processing;

namespace PictureSharp;

/// <summary>Reconstruction of HDR pixels from a gain map, conversion back to SDR and gain map generation.</summary>
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

        /// <summary>
        /// Creates an SDR image with an attached ISO 21496-1 gain map that reproduces the HDR pixels: the base is
        /// <c>hdr.ToneMap(options.ToneMapping)</c> and the map is computed as libultrahdr computes it. Save the result
        /// as JPEG with <see cref="Formats.Jpeg.JpegGainMapMode.Require"/> to write an Ultra HDR file; <c>ToHdr</c>, geometric
        /// <c>Mutate</c> operations and <c>GetGainMap</c> work as for a loaded image with a gain map.
        /// </summary>
        /// <example>
        /// <code>
        /// using Image sdr = hdr.ToGainMapImage();
        /// sdr.SaveAsJpeg(path, new JpegEncoderOptions { Quality = 95, GainMap = JpegGainMapMode.Require });
        /// </code>
        /// </example>
        public Image ToGainMapImage(GainMapOptions? options = null, CancellationToken cancellationToken = default) =>
            GainMapGeneration.ToGainMapImage(hdr, null, options, cancellationToken);

        /// <summary>
        /// Creates a copy of <paramref name="sdrBase"/> with an attached ISO 21496-1 gain map that reproduces the HDR
        /// pixels from it, computed as libultrahdr computes it. The base must have the HDR image's size and one frame,
        /// in sRGB or Display P3 by its color description (no description means sRGB); an attached gain map is
        /// replaced. <see cref="GainMapOptions.ToneMapping"/> is not used.
        /// </summary>
        /// <exception cref="ArgumentException">The base has another size or more than one frame.</exception>
        /// <exception cref="NotSupportedException">The base is not in sRGB or Display P3.</exception>
        /// <exception cref="ImageFormatException">The base's ICC profile is malformed.</exception>
        public Image ToGainMapImage(Image sdrBase, GainMapOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(sdrBase);
            return GainMapGeneration.ToGainMapImage(hdr, sdrBase, options, cancellationToken);
        }
    }
}
