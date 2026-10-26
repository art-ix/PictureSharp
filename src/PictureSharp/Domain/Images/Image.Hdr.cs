using PictureSharp.Domain.Hdr;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>
    /// The gain map attached by a load with <c>DecoderOptions.LoadGainMap</c>. Geometric edits replace it with the map
    /// carried through the same geometry; other pixel or frame changes remove it, because it describes the base pixels.
    /// </summary>
    internal GainMapAttachment? AttachedGainMap
    {
        get { ThrowIfDisposed(); return field; }
        set { ThrowIfDisposed(); field = value; }
    }

    /// <summary>
    /// The HDR gain map of the file this image was loaded from, or null when <c>DecoderOptions.LoadGainMap</c> was not
    /// set, the file declares none, or the image was changed since loading. <c>Resize</c> (except padding),
    /// <c>Thumbnail</c>, <c>Crop</c>, <c>AutoOrient</c>, quarter-turn rotations and flips keep the map and edit it with the
    /// base; other <c>Mutate</c> operations and frame changes remove it; <see cref="Clone()"/> keeps it. Corrupt or
    /// unsupported HDR data does not fail the load; it is reported here. The encodable map data is returned by
    /// <c>GetGainMap()</c>.
    /// </summary>
    public GainMapInfo? GainMap => AttachedGainMap?.Info;

    /// <summary>
    /// Whether <c>ToHdr</c> can reconstruct HDR pixels: a gain map is attached and its headers are valid and supported.
    /// <c>ToHdr</c> uses the current base pixels, so writes through pixel views are not detected.
    /// </summary>
    public bool HasHdr => AttachedGainMap?.CanDecode == true;
}
