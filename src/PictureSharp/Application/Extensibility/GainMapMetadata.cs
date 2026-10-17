using System.Drawing;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Formats;

/// <summary>
/// The parsed metadata of an HDR gain map, independent of the container that stores it: the ISO 21496-1 payload or the
/// Apple model. A format plugin parses it and attaches it with <see cref="ImageDecodeContext.AttachGainMap(GainMapMetadata, GainMapImage)"/>, so
/// <c>ToHdr</c> and <c>ToneMap</c> work as for JPEG files. Immutable and thread-safe.
/// </summary>
public sealed class GainMapMetadata
{
    private GainMapMetadata(GainMapParameters parameters) => Parameters = parameters;

    /// <summary>The metadata model: <see cref="HdrGainMapKinds.Iso21496"/> or <see cref="HdrGainMapKinds.Apple"/>.</summary>
    public HdrGainMapKinds Kind => Parameters.Kind;

    /// <summary>The number of map channels the metadata describes: 1 (gray map) or 3 (RGB map).</summary>
    public int ChannelCount => Parameters.ChannelCount;

    /// <summary>The maximum headroom as a linear ratio to SDR white (4 means two stops above SDR white).</summary>
    public float Headroom => float.Exp2(Math.Max(Parameters.AlternateHeadroom, Parameters.BaseHeadroom));

    internal GainMapParameters Parameters { get; }

    /// <summary>
    /// Parses ISO 21496-1 gain map metadata: the binary payload that starts with the minimum and writer versions (as in
    /// the JPEG APP2 segment after its URN, or a HEIF <c>tmap</c> item after its version byte).
    /// </summary>
    /// <param name="metadata">The ISO 21496-1 payload.</param>
    /// <param name="mapChannels">The channels of the stored map: 1 for a gray map, 3 for an RGB map.</param>
    /// <param name="alternateIcc">
    /// The ICC profile of the alternate rendition, which gives the primaries the gain is applied in when the payload does
    /// not use the base color space; empty when there is none.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mapChannels"/> is not 1 or 3.</exception>
    /// <exception cref="ImageFormatException">The payload is truncated or invalid.</exception>
    /// <exception cref="NotSupportedException">
    /// The version, an HDR base rendition or the alternate color space (other than sRGB or Display P3) is not supported.
    /// </exception>
    public static GainMapMetadata ParseIso(ReadOnlySpan<byte> metadata, int mapChannels, ReadOnlySpan<byte> alternateIcc = default)
    {
        if (mapChannels is not (1 or 3))
        {
            throw new ArgumentOutOfRangeException(nameof(mapChannels), mapChannels, "A gain map has 1 or 3 channels.");
        }

        return new(GainMapMetadataParser.ReadIso(metadata, mapChannels, alternateIcc, format: null));
    }

    /// <summary>
    /// Parses the Apple HDR gain map model (iOS 14–17): the headroom from the Apple MakerNote in the base image's EXIF
    /// (tags 33 and 48), or from the map's <c>HDRGainMapHeadroom</c> XMP when the MakerNote lacks them, and the map
    /// version from its <c>HDRGainMapVersion</c> XMP.
    /// </summary>
    /// <param name="exif">The base image's EXIF data as a TIFF structure (starting with II or MM); empty when there is none.</param>
    /// <param name="mapXmp">The XMP packet of the gain map image.</param>
    /// <exception cref="ImageFormatException">The headroom is missing or invalid.</exception>
    /// <exception cref="NotSupportedException">The map version is not supported.</exception>
    public static GainMapMetadata ParseApple(ReadOnlySpan<byte> exif, ReadOnlySpan<byte> mapXmp) =>
        new(GainMapMetadataParser.ReadApple(exif, mapXmp, format: null));

    /// <summary>
    /// The description <c>Identify</c> reports for a map of <paramref name="mapSize"/> with this metadata: detected and
    /// selected model <see cref="Kind"/>, the headroom and <see cref="HdrInfo.CanDecode"/> set. Use <c>with</c> to add
    /// further detected models.
    /// </summary>
    public HdrInfo ToHdrInfo(Size mapSize) => new()
    {
        Detected = Kind,
        Selected = Kind,
        GainMapSize = mapSize,
        Headroom = Headroom,
        CanDecode = true,
    };
}
