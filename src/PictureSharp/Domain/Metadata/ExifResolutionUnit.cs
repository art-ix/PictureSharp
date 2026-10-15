namespace PictureSharp;

/// <summary>EXIF resolution unit values (TIFF tag 0x0128).</summary>
public enum ExifResolutionUnit
{
    /// <summary>No absolute unit; the resolution only defines the aspect ratio.</summary>
    None = 1,
    /// <summary>Dots per inch.</summary>
    Inch = 2,
    /// <summary>Dots per centimeter.</summary>
    Centimeter = 3,
}
