namespace RasterSharp;

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
