namespace PictureSharp;

/// <summary>The colour model of an image's colour samples (see <see cref="ImageColorInfo.Model"/>).</summary>
public enum ImageColorModel
{
    /// <summary>
    /// A model none of the other values describes, for example TIFF LogLuv, CFA, LinearRaw or a non-CMYK ink set, or
    /// JPEG XL with the XYB colour space declared as the image colour space.
    /// </summary>
    Other = 0,

    /// <summary>One grey or luminance sample (PNG grey, TIFF WhiteIsZero and BlackIsZero, monochrome HEVC).</summary>
    Gray = 1,

    /// <summary>Red, green and blue samples, also when the file codes them as YCbCr or XYB.</summary>
    Rgb = 2,

    /// <summary>One index per pixel into a colour table.</summary>
    Palette = 3,

    /// <summary>Cyan, magenta, yellow and black samples, also when the file codes them as YCCK.</summary>
    Cmyk = 4,

    /// <summary>CIE L*a*b* samples (TIFF CIELab, ICCLab and ITULab, PSD Lab).</summary>
    Lab = 5,
}
