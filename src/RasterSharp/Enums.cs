namespace RasterSharp;

/// <summary>Rotation and flip operations. Values match System.Drawing.RotateFlipType.</summary>
public enum RotateFlipType
{
    /// <summary>No rotation, no flip.</summary>
    RotateNoneFlipNone = 0,
    /// <summary>90° clockwise, no flip.</summary>
    Rotate90FlipNone = 1,
    /// <summary>180°, no flip.</summary>
    Rotate180FlipNone = 2,
    /// <summary>270° clockwise, no flip.</summary>
    Rotate270FlipNone = 3,
    /// <summary>No rotation, horizontal flip.</summary>
    RotateNoneFlipX = 4,
    /// <summary>90° clockwise, horizontal flip.</summary>
    Rotate90FlipX = 5,
    /// <summary>180°, horizontal flip (same as vertical flip).</summary>
    Rotate180FlipX = 6,
    /// <summary>270° clockwise, horizontal flip.</summary>
    Rotate270FlipX = 7,
    /// <summary>Same as <see cref="Rotate180FlipX"/>.</summary>
    RotateNoneFlipY = Rotate180FlipX,
    /// <summary>Same as <see cref="Rotate270FlipX"/>.</summary>
    Rotate90FlipY = Rotate270FlipX,
    /// <summary>Same as <see cref="RotateNoneFlipX"/>.</summary>
    Rotate180FlipY = RotateNoneFlipX,
    /// <summary>Same as <see cref="Rotate90FlipX"/>.</summary>
    Rotate270FlipY = Rotate90FlipX,
    /// <summary>Same as <see cref="Rotate180FlipNone"/>.</summary>
    RotateNoneFlipXY = Rotate180FlipNone,
    /// <summary>Same as <see cref="Rotate270FlipNone"/>.</summary>
    Rotate90FlipXY = Rotate270FlipNone,
    /// <summary>Same as <see cref="RotateNoneFlipNone"/>.</summary>
    Rotate180FlipXY = RotateNoneFlipNone,
    /// <summary>Same as <see cref="Rotate90FlipNone"/>.</summary>
    Rotate270FlipXY = Rotate90FlipNone,
}

/// <summary>Access mode for <see cref="Image.LockBits"/>. Values match System.Drawing.Imaging.ImageLockMode.</summary>
[Flags]
public enum ImageLockMode
{
    /// <summary>The buffer is only read; changes are discarded on unlock.</summary>
    ReadOnly = 1,
    /// <summary>The buffer is only written; it is not filled with the current pixels and is copied back on unlock.</summary>
    WriteOnly = 2,
    /// <summary>The buffer is filled with the current pixels and copied back on unlock.</summary>
    ReadWrite = ReadOnly | WriteOnly,
}

/// <summary>EXIF orientation tag values (TIFF tag 0x0112).</summary>
public enum ExifOrientation
{
    /// <summary>Tag missing or invalid.</summary>
    Unknown = 0,
    /// <summary>Normal.</summary>
    TopLeft = 1,
    /// <summary>Mirrored horizontally.</summary>
    TopRight = 2,
    /// <summary>Rotated 180°.</summary>
    BottomRight = 3,
    /// <summary>Mirrored vertically.</summary>
    BottomLeft = 4,
    /// <summary>Mirrored horizontally and rotated 270° clockwise.</summary>
    LeftTop = 5,
    /// <summary>Rotated 90° clockwise.</summary>
    RightTop = 6,
    /// <summary>Mirrored horizontally and rotated 90° clockwise.</summary>
    RightBottom = 7,
    /// <summary>Rotated 270° clockwise.</summary>
    LeftBottom = 8,
}

/// <summary>Compression used by a WebP file.</summary>
public enum WebPCompression
{
    /// <summary>Not a WebP file.</summary>
    None = 0,
    /// <summary>VP8 lossy bitstream (optionally with an ALPH chunk).</summary>
    Lossy,
    /// <summary>VP8L lossless bitstream.</summary>
    Lossless,
    /// <summary>Animation whose frames use both lossy and lossless bitstreams.</summary>
    Mixed,
}

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
