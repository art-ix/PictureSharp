using System.Drawing;

namespace PictureSharp;

/// <summary>
/// The HDR gain map declared by an image file, as reported by <c>Image.Identify</c> and <see cref="Image.Hdr"/> when
/// <c>DecoderOptions.LoadGainMap</c> is set. Describes the headers only; the map pixels are decoded by <c>ToHdr</c>.
/// </summary>
public sealed record HdrInfo
{
    /// <summary>Every gain map model the file declares.</summary>
    public HdrGainMapKinds Detected { get; init; }

    /// <summary>
    /// The model used for reconstruction (ISO 21496-1 wins over Apple), or <see cref="HdrGainMapKinds.None"/> when
    /// <see cref="CanDecode"/> is false.
    /// </summary>
    public HdrGainMapKinds Selected { get; init; }

    /// <summary>
    /// The stored size of the gain map, or <see cref="Size.Empty"/> when it is unknown. After geometric edits that keep
    /// the map (<c>Resize</c>, <c>Crop</c>, quarter turns, flips) it is the size of the edited map.
    /// </summary>
    public Size GainMapSize { get; init; }

    /// <summary>
    /// The maximum headroom of the selected model as a linear ratio to SDR white (4 means two stops above SDR white),
    /// or null when it is unknown.
    /// </summary>
    public float? Headroom { get; init; }

    /// <summary>
    /// Whether the headers of the selected model were valid and supported, so <c>ToHdr</c> can be called. The entropy-coded
    /// data of the map is not checked.
    /// </summary>
    public bool CanDecode { get; init; }

    /// <summary>Why the gain map cannot be decoded, for diagnostics only; null when <see cref="CanDecode"/> is true.</summary>
    public string? UnsupportedReason { get; init; }
}
