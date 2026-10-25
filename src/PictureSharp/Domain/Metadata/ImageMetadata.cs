using PictureSharp.Domain.Metadata;
using PictureSharp.Formats;

namespace PictureSharp;

/// <summary>Resolution, color profile and metadata blocks carried by an image.</summary>
public sealed class ImageMetadata
{
    private byte[]? _exif;
    private byte[]? _icc;
    private byte[]? _xmp;
    private FormatMetadata[] _formats = [];

    /// <summary>The live typed EXIF view; an absent block behaves as an empty profile.</summary>
    public ExifProfile ExifProfile => field ??= new(this);

    /// <summary>Horizontal resolution in dots per inch. Default: 96.</summary>
    public float HorizontalResolution
    {
        get;
        set => field = CheckResolution(value);
    } = 96f;

    /// <summary>Vertical resolution in dots per inch. Default: 96.</summary>
    public float VerticalResolution
    {
        get;
        set => field = CheckResolution(value);
    } = 96f;

    /// <summary>
    /// Gamma of the encoded values (PNG <c>gAMA</c>), e.g. 0.45455 for a 1/2.2 encoding: linear light is the encoded
    /// value raised to 1/<see cref="Gamma"/>. Used to convert colors only without an ICC profile and
    /// <see cref="SrgbRenderingIntent"/>. The range is that of <c>gAMA</c>: 0.00001 to 21474.83647. Default: null.
    /// </summary>
    public float? Gamma
    {
        get;
        set => field = value is null or (>= 0.00001f and <= 21474.83647f)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Gamma), value, "Gamma must be between 0.00001 and 21474.83647, as in PNG gAMA.");
    }

    /// <summary>
    /// White point and primaries (PNG <c>cHRM</c>). Used to convert colors only without an ICC profile and
    /// <see cref="SrgbRenderingIntent"/>. Default: null.
    /// </summary>
    public CieChromaticities? Chromaticities
    {
        get;
        set => field = value is null || value.Value.WhiteY > 0
            ? value
            : throw new ArgumentException("Uninitialized chromaticities.", nameof(Chromaticities));
    }

    /// <summary>
    /// Set when the image declares the sRGB color space (PNG <c>sRGB</c> chunk) with this rendering intent; such an
    /// image needs no conversion unless it also has an ICC profile, which takes precedence. Default: null.
    /// </summary>
    public RenderingIntent? SrgbRenderingIntent
    {
        get;
        set => field = value is null || Enum.IsDefined(value.Value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(SrgbRenderingIntent), value, "Unknown rendering intent.");
    }

    /// <summary>Whether <see cref="Gamma"/>, <see cref="Chromaticities"/> or <see cref="SrgbRenderingIntent"/> is set.</summary>
    public bool HasColorDescription => Gamma is not null || Chromaticities is not null || SrgbRenderingIntent is not null;

    internal bool SameColorDescription(ImageMetadata other) =>
        Gamma == other.Gamma && Chromaticities == other.Chromaticities && SrgbRenderingIntent == other.SrgbRenderingIntent;

    /// <summary>Removes <see cref="Gamma"/>, <see cref="Chromaticities"/> and <see cref="SrgbRenderingIntent"/>.</summary>
    public void ClearColorDescription()
    {
        Gamma = null;
        Chromaticities = null;
        SrgbRenderingIntent = null;
    }

    /// <summary>Raw ICC snapshot. Both get and set copy the block; the profile is preserved without applying it.</summary>
    public byte[]? IccProfile { get => (byte[]?)_icc?.Clone(); set => _icc = (byte[]?)value?.Clone(); }

    /// <summary>Raw EXIF data as a TIFF stream (starting with "II*\0" or "MM\0*"), without the JPEG "Exif\0\0" prefix. Both get and set copy the block.</summary>
    public byte[]? Exif
    {
        get => (byte[]?)_exif?.Clone();
        set => _exif = value is null ? null : ExifReader.StripJpegPrefix((byte[])value.Clone());
    }

    /// <summary>Raw XMP snapshot (UTF-8 XML); both get and set copy the block.</summary>
    public byte[]? Xmp { get => (byte[]?)_xmp?.Clone(); set => _xmp = (byte[]?)value?.Clone(); }

    /// <summary>Orientation read from the EXIF block; assigning creates the tag if absent. Malformed existing EXIF is rejected. Reading returns <see cref="ExifOrientation.Unknown"/> when absent.</summary>
    public ExifOrientation Orientation
    {
        get => _exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(_exif);
        set
        {
            _exif = ExifReader.SetOrientation(_exif, value);
        }
    }

    /// <summary>Whether no ICC, EXIF or XMP block and no <see cref="FormatMetadata"/> is present.</summary>
    public bool IsEmpty => _icc is null && _exif is null && _xmp is null && _formats.Length == 0;

    /// <summary>Removes the ICC, EXIF and XMP blocks and all <see cref="FormatMetadata"/>; the resolution and color description are kept.</summary>
    public void Clear()
    {
        _icc = null;
        _exif = null;
        _xmp = null;
        _formats = [];
    }

    /// <summary>
    /// Metadata of plugin formats, at most one per type, in the order it was set. Removed by operations that move pixels;
    /// see <see cref="Formats.FormatMetadata"/>. Default: empty.
    /// </summary>
    public IReadOnlyList<FormatMetadata> FormatMetadata => _formats.Length == 0 ? [] : Array.AsReadOnly(_formats);

    /// <summary>The format metadata of type <typeparamref name="T"/>, or null when there is none.</summary>
    public T? GetFormatMetadata<T>() where T : FormatMetadata
    {
        foreach (FormatMetadata entry in _formats)
        {
            if (entry is T match)
                return match;
        }

        return null;
    }

    /// <summary>Adds format metadata, replacing the entry of the same type.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    public void SetFormatMetadata(FormatMetadata value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int index = Array.FindIndex(_formats, entry => entry.GetType() == value.GetType());
        // Copy on write, so a list returned earlier does not change.
        FormatMetadata[] copy = index < 0 ? [.. _formats, value] : (FormatMetadata[])_formats.Clone();
        if (index >= 0)
            copy[index] = value;
        _formats = copy;
    }

    /// <summary>Removes the format metadata of type <typeparamref name="T"/>; returns false when there was none.</summary>
    public bool RemoveFormatMetadata<T>() where T : FormatMetadata
    {
        int index = Array.FindIndex(_formats, static entry => entry is T);
        if (index < 0)
            return false;

        _formats = [.. _formats.AsSpan(0, index), .. _formats.AsSpan(index + 1)];
        return true;
    }

    /// <summary>Removes all format metadata, after an operation that moved the pixels it may describe.</summary>
    internal void PixelsMoved() => _formats = [];

    /// <summary>Returns a deep copy.</summary>
    public ImageMetadata Clone() => new()
    {
        HorizontalResolution = HorizontalResolution,
        VerticalResolution = VerticalResolution,
        Gamma = Gamma,
        Chromaticities = Chromaticities,
        SrgbRenderingIntent = SrgbRenderingIntent,
        _icc = (byte[]?)_icc?.Clone(),
        _exif = (byte[]?)_exif?.Clone(),
        _xmp = (byte[]?)_xmp?.Clone(),
        // The entries are immutable records and the array is replaced on every change, so it can be shared.
        _formats = _formats,
    };

    // Internal ownership transfer avoids copying freshly decoded blocks or allocating on encoding.
    internal byte[]? IccData { get => _icc; set => _icc = value; }
    internal byte[]? ExifData { get => _exif; set => _exif = value is null ? null : ExifReader.StripJpegPrefix(value); }
    internal byte[]? XmpData { get => _xmp; set => _xmp = value; }

    /// <summary>Updates an existing supported orientation tag; returns false if absent or malformed.</summary>
    public bool TrySetOrientation(ExifOrientation orientation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan((int)orientation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)orientation, 8);
        return _exif is not null && ExifReader.TrySetOrientation(_exif, orientation);
    }

    /// <summary>Replaces one raw metadata block with an owned copy. EXIF accepts an optional JPEG prefix.</summary>
    public void SetBlock(MetadataKinds kind, ReadOnlySpan<byte> data)
    {
        _ = GetBlockLength(kind); // Validate before allocating.
        byte[] copy = data.ToArray();
        switch (kind)
        {
            case MetadataKinds.Icc: _icc = copy; break;
            case MetadataKinds.Exif: _exif = ExifReader.StripJpegPrefix(copy); break;
            case MetadataKinds.Xmp: _xmp = copy; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "Select exactly one metadata kind.");
        }
    }

    /// <summary>Copies one raw block into a caller-owned buffer and returns its length; missing blocks have length zero.</summary>
    public int CopyBlockTo(MetadataKinds kind, Span<byte> destination)
    {
        byte[]? block = GetBlock(kind);
        if (block is null) return 0;
        block.CopyTo(destination);
        return block.Length;
    }

    /// <summary>Returns the byte length of one metadata block, or zero if absent, without copying.</summary>
    public int GetBlockLength(MetadataKinds kind) => GetBlock(kind)?.Length ?? 0;

    private byte[]? GetBlock(MetadataKinds kind) => kind switch
    {
        MetadataKinds.Icc => _icc,
        MetadataKinds.Exif => _exif,
        MetadataKinds.Xmp => _xmp,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Select exactly one metadata kind."),
    };

    /// <summary>Removes selected raw blocks, preserving resolution.</summary>
    public void RemoveBlocks(MetadataKinds kinds)
    {
        if ((kinds & ~MetadataKinds.All) != 0) throw new ArgumentOutOfRangeException(nameof(kinds));
        if ((kinds & MetadataKinds.Icc) != 0) _icc = null;
        if ((kinds & MetadataKinds.Exif) != 0) _exif = null;
        if ((kinds & MetadataKinds.Xmp) != 0) _xmp = null;
    }

    private static float CheckResolution(float dpi, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        dpi > 0 && float.IsFinite(dpi) ? dpi : throw new ArgumentOutOfRangeException(name, dpi, "Resolution must be a positive finite number.");
}
