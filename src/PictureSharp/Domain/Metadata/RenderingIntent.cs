namespace PictureSharp;

/// <summary>ICC rendering intents, as stored by the PNG <c>sRGB</c> chunk (ICC.1 §7.2.15).</summary>
public enum RenderingIntent
{
    /// <summary>Perceptual: compresses the gamut for pleasing results.</summary>
    Perceptual = 0,
    /// <summary>Relative colorimetric: keeps in-gamut colors, maps the media white to the destination white.</summary>
    RelativeColorimetric = 1,
    /// <summary>Saturation: favors vivid colors, for graphics.</summary>
    Saturation = 2,
    /// <summary>Absolute colorimetric: keeps colors including the media white.</summary>
    AbsoluteColorimetric = 3,
}
