namespace PictureSharp.Formats;

/// <summary>
/// An HDR gain map that an encoder can write with its image, from <c>GetGainMap</c>: the ISO 21496-1 metadata (written
/// with <see cref="GainMapMetadata.ToIso"/>) and the map in the geometry of the image's current pixels.
/// </summary>
/// <param name="Metadata">The map's metadata; <see cref="GainMapMetadata.Kind"/> is <see cref="HdrGainMapKinds.Iso21496"/>.</param>
/// <param name="Map">
/// The map. When the image was not rotated or edited since it was loaded and the map was attached by a format plugin,
/// this is the plugin's own <see cref="GainMapImage"/> instance, so the plugin can copy its compressed data instead of
/// encoding it again. Otherwise <see cref="GainMapImage.Decode"/> returns a new image (disposed by the caller) of
/// <see cref="GainMapImage.Size"/> in the orientation of the image's pixels; a gray map has equal R, G and B.
/// </param>
public sealed record EncodableGainMap(GainMapMetadata Metadata, GainMapImage Map);
