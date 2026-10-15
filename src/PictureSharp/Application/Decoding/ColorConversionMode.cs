namespace PictureSharp;

/// <summary>What <see cref="DecoderOptions.ColorConversion"/> does with an image's color description.</summary>
public enum ColorConversionMode
{
    /// <summary>Keep the decoded values and the ICC profile or color description unchanged.</summary>
    None = 0,

    /// <summary>
    /// Convert to sRGB like <c>ConvertToSrgb()</c> and remove the profile and color description. Images without one
    /// are already sRGB and stay unchanged.
    /// </summary>
    ToSrgb = 1,
}
