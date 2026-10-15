using PictureSharp.Domain.Metadata;

namespace PictureSharp;

/// <summary>Resolution, color profile and metadata blocks carried by an image.</summary>
public sealed class ImageMetadata
{
    private byte[]? _exif;
    private byte[]? _icc;
    private byte[]? _xmp;

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

    /// <summary>Whether no ICC, EXIF or XMP block is present.</summary>
    public bool IsEmpty => _icc is null && _exif is null && _xmp is null;

    /// <summary>Removes the ICC, EXIF and XMP blocks; the resolution is kept.</summary>
    public void Clear()
    {
        _icc = null;
        _exif = null;
        _xmp = null;
    }

    /// <summary>Returns a deep copy.</summary>
    public ImageMetadata Clone() => new()
    {
        HorizontalResolution = HorizontalResolution,
        VerticalResolution = VerticalResolution,
        _icc = (byte[]?)_icc?.Clone(),
        _exif = (byte[]?)_exif?.Clone(),
        _xmp = (byte[]?)_xmp?.Clone(),
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
