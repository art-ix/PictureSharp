namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>Matrix/TRC color models for color descriptions that are not ICC profiles (PNG gAMA and cHRM).</summary>
internal static class ColorModels
{
    /// <summary>The D50-adapted RGB→XYZ matrix of <paramref name="c"/>, or null when it is degenerate.</summary>
    public static ColorMatrix? Colorants(CieChromaticities c) =>
        ColorMatrix.TryRgbToD50((c.RedX, c.RedY), (c.GreenX, c.GreenY), (c.BlueX, c.BlueY), (c.WhiteX, c.WhiteY));

    /// <summary>
    /// The model of a gamma and/or chromaticities description: missing chromaticities mean sRGB primaries and white,
    /// a missing gamma means the sRGB transfer function. Linear light is the encoded value raised to 1/gamma.
    /// </summary>
    // Equivalent matrix/TRC profile of PNG gAMA/cHRM (PNG 3rd edition §11.3.3), as libpng and browsers interpret them.
    public static IccColorModel? FromDescription(float? gamma, CieChromaticities? chromaticities)
    {
        ToneCurve curve = gamma is { } g ? ToneCurve.Power(1.0 / g) : ToneCurve.Srgb;
        ColorMatrix? colorants = chromaticities is { } c ? Colorants(c) : ColorMatrix.SrgbToD50;
        return colorants is null ? null : new IccColorModel(curve, curve, curve, colorants);
    }
}
