using System.Drawing;

namespace PictureSharp.Domain.Hdr;

/// <summary>Why an attached gain map cannot be reconstructed; selects the exception <c>ToHdr</c> throws.</summary>
internal enum GainMapFailure
{
    None = 0,
    Corrupt = 1,
    Unsupported = 2,
}

/// <summary>
/// Reconstruction parameters of one gain map model, already converted from the file's encoding. ISO 21496-1 values
/// are log2 except gamma and offsets; per-channel arrays always have three entries (a single-channel map repeats them).
/// </summary>
internal sealed record GainMapParameters
{
    public required HdrGainMapKinds Kind { get; init; }

    /// <summary>1 for a gray map, 3 for an RGB map.</summary>
    public int ChannelCount { get; init; } = 1;

    public float[] GainMapMin { get; init; } = [0, 0, 0];
    public float[] GainMapMax { get; init; } = [0, 0, 0];
    public float[] Gamma { get; init; } = [1, 1, 1];
    public float[] BaseOffset { get; init; } = [0, 0, 0];
    public float[] AlternateOffset { get; init; } = [0, 0, 0];

    /// <summary>log2 headroom of the base rendition (ISO) — 0 for an SDR base.</summary>
    public float BaseHeadroom { get; init; }

    /// <summary>log2 headroom of the alternate rendition (ISO), or log2 of the Apple headroom.</summary>
    public float AlternateHeadroom { get; init; }

    /// <summary>
    /// The primaries in which the gain is applied: the base primaries for ISO <c>use_base_colour_space</c> and Apple,
    /// otherwise the primaries of the alternate rendition from the map's ICC profile.
    /// </summary>
    public HdrColorSpace? MultiplicationSpace { get; init; }

    /// <summary>
    /// The ISO <c>use_base_colour_space</c> flag. Kept apart from <see cref="MultiplicationSpace"/>, which loaders fill
    /// with the base primaries, so the metadata can be written back unchanged.
    /// </summary>
    public bool UseBaseColorSpace { get; init; } = true;
}

/// <summary>Decodes the compressed map of a gain map attachment; implemented by the codec that found the map.</summary>
internal interface IGainMapDecoder
{
    /// <summary>Decodes <paramref name="data"/> reduced by <paramref name="scale"/> (1, 2, 4 or 8) in the inverse DCT.</summary>
    DecodedGainMap Decode(ReadOnlyMemory<byte> data, int scale, int maxDegreeOfParallelism, CancellationToken cancellationToken);

    /// <summary>Whether <see cref="Decode"/> accepts scales above 1; otherwise the map is always decoded at full size.</summary>
    bool CanScale => true;
}

/// <summary>Map samples as decoded (gray maps repeat the value in R, G and B).</summary>
internal readonly record struct DecodedGainMap(Rgba32[] Pixels, int Width, int Height);

/// <summary>
/// A gain map attached to a loaded image: header description, compressed map and how the current base pixels relate to
/// the stored base image. Immutable, so clones may share it. The model only carries it; decoding and the
/// reconstruction math live in the higher layers.
/// </summary>
internal sealed record GainMapAttachment
{
    public required HdrInfo Info { get; init; }

    public GainMapFailure Failure { get; init; }

    public GainMapParameters? Parameters { get; init; }

    /// <summary>The compressed map, owned by the attachment (a copy of the input).</summary>
    public ReadOnlyMemory<byte> MapData { get; init; }

    public IGainMapDecoder? Decoder { get; init; }

    /// <summary>
    /// The map carried through geometric edits, in the geometry of the current base pixels; null while
    /// <see cref="MapData"/> still describes them. When set, <see cref="StoredSize"/> is the current base size,
    /// <see cref="AppliedOrientation"/> is <see cref="ExifOrientation.Unknown"/> and <c>Info.GainMapSize</c> is its size.
    /// </summary>
    public DecodedGainMap? EditedMap { get; init; }

    /// <summary>The stored size of the base image, before thumbnails and orientation.</summary>
    public Size StoredSize { get; init; }

    /// <summary>The EXIF orientation applied to the base pixels while loading; <see cref="ExifOrientation.Unknown"/> when none.</summary>
    public ExifOrientation AppliedOrientation { get; init; }

    /// <summary>The primaries of the current base pixels.</summary>
    public HdrColorSpace BaseSpace { get; init; }

    /// <summary>
    /// Linear values of the 256 codes of the R, G and B channels of the current base pixels (768 entries), or null for
    /// the sRGB transfer function.
    /// </summary>
    public float[]? BaseLinearization { get; init; }

    /// <summary>Whether reconstruction can run.</summary>
    public bool CanDecode => Failure == GainMapFailure.None && Parameters is not null && Decoder is not null;

    /// <summary>An attachment that only reports why the declared gain map cannot be used.</summary>
    public static GainMapAttachment Failed(HdrInfo info, GainMapFailure failure, string reason) => new()
    {
        Info = info with { Selected = HdrGainMapKinds.None, CanDecode = false, UnsupportedReason = reason },
        Failure = failure,
    };
}
