namespace PictureSharp;

/// <summary>Metadata kinds that encoders may write.</summary>
[Flags]
public enum MetadataKinds
{
    /// <summary>No metadata.</summary>
    None = 0,
    /// <summary>ICC color profile.</summary>
    Icc = 1,
    /// <summary>EXIF block.</summary>
    Exif = 2,
    /// <summary>XMP packet.</summary>
    Xmp = 4,
    /// <summary>All metadata.</summary>
    All = Icc | Exif | Xmp,
}
