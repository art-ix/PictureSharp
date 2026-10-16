namespace PictureSharp;

/// <summary>The linear-light RGB color space of <see cref="HdrImage"/> pixels. Both use the D65 white point.</summary>
public enum HdrColorSpace
{
    /// <summary>Linear sRGB (BT.709 primaries), extended: components may exceed 1 or be negative.</summary>
    LinearSrgb = 0,

    /// <summary>Linear Display P3 (DCI-P3 primaries, D65 white).</summary>
    LinearDisplayP3 = 1,
}
