using PictureSharp.Application.Hdr;

namespace PictureSharp.Formats;

/// <summary>Access to the attached HDR gain map for encoders of format plugins.</summary>
public static class GainMapExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// The HDR gain map an encoder writes with the image: attached by a load with
        /// <see cref="DecoderOptions.LoadGainMap"/>, kept through geometric operations and described by ISO 21496-1
        /// metadata. Nothing is decoded until <see cref="GainMapImage.Decode"/>.
        /// </summary>
        /// <param name="require">
        /// False (as <c>JpegGainMapMode.Preserve</c>): null when no map can be written. True (as
        /// <c>JpegGainMapMode.Require</c>): throw instead.
        /// </param>
        /// <example>
        /// <code>
        /// if (image.GetGainMap() is { } gainMap)
        /// {
        ///     byte[] iso = gainMap.Metadata.ToIso();
        ///     using Image map = gainMap.Map.Decode(maxDegreeOfParallelism: -1, cancellationToken);
        /// }
        /// </code>
        /// </example>
        /// <exception cref="InvalidOperationException"><paramref name="require"/> is true and no gain map is attached.</exception>
        /// <exception cref="NotSupportedException">
        /// <paramref name="require"/> is true and the map is valid but cannot be written (Apple metadata only, or a
        /// variant that cannot be reconstructed).
        /// </exception>
        /// <exception cref="ImageFormatException"><paramref name="require"/> is true and the map is corrupt.</exception>
        public EncodableGainMap? GetGainMap(bool require = false) => GainMapEncoding.ForEncoding(image, require);
    }
}
