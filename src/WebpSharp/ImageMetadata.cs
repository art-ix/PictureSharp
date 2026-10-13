using WebpSharp.Formats.Metadata;

namespace WebpSharp;

/// <summary>Color profile and metadata blocks carried by an image.</summary>
public sealed class ImageMetadata
{
    private byte[]? _exif;

    /// <summary>Raw ICC color profile. The library carries it over but does not apply it.</summary>
    public byte[]? IccProfile { get; set; }

    /// <summary>Raw EXIF data as a TIFF stream (starting with "II*\0" or "MM\0*"), without the JPEG "Exif\0\0" prefix.</summary>
    public byte[]? Exif
    {
        get => _exif;
        set => _exif = value is null ? null : ExifReader.StripJpegPrefix(value);
    }

    /// <summary>Raw XMP packet (UTF-8 XML).</summary>
    public byte[]? Xmp { get; set; }

    /// <summary>Orientation read from the EXIF block, or <see cref="ExifOrientation.Unknown"/> when absent.</summary>
    public ExifOrientation Orientation
    {
        get => _exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(_exif);
        set
        {
            if (_exif is not null)
            {
                ExifReader.TrySetOrientation(_exif, value);
            }
        }
    }

    /// <summary>Whether any metadata is present.</summary>
    public bool IsEmpty => IccProfile is null && _exif is null && Xmp is null;

    /// <summary>Removes all metadata.</summary>
    public void Clear()
    {
        IccProfile = null;
        _exif = null;
        Xmp = null;
    }

    /// <summary>Returns a deep copy.</summary>
    public ImageMetadata Clone() => new()
    {
        IccProfile = (byte[]?)IccProfile?.Clone(),
        _exif = (byte[]?)_exif?.Clone(),
        Xmp = (byte[]?)Xmp?.Clone(),
    };
}
