namespace PictureSharp;

/// <summary>The numeric type of stored samples (see <see cref="ImageColorInfo.SampleFormat"/>).</summary>
public enum ImageSampleFormat
{
    /// <summary>Unsigned integers.</summary>
    UnsignedInteger = 0,

    /// <summary>Two's complement signed integers (TIFF SampleFormat 2).</summary>
    SignedInteger = 1,

    /// <summary>Floating point; the width is <see cref="ImageColorInfo.BitsPerSample"/> (16, 24, 32 or 64).</summary>
    Float = 2,

    /// <summary>A type none of the other values describes (TIFF complex integer and complex float).</summary>
    Other = 3,
}
