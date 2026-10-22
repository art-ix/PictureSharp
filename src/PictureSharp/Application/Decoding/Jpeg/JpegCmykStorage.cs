namespace PictureSharp.Formats.Jpeg;

/// <summary>How the values of a four-component (CMYK or YCCK) JPEG are to be read.</summary>
public enum JpegCmykStorage
{
    /// <summary>
    /// Inverted, as Adobe applications write CMYK JPEG files: a stored 0 is full ink (the default, as Pillow and
    /// browsers assume for every four-component JPEG).
    /// </summary>
    AdobeInverted,
    /// <summary>
    /// Ink amounts, a stored 0 being no ink, as TIFF stores CMYK and libtiff writes CMYK JPEG strips (photometric 5).
    /// </summary>
    Plain,
}
