using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>Primaries of the HDR color spaces and the linear-light matrices between them.</summary>
internal static class HdrColorSpaces
{
    // Display P3: DCI-P3 primaries with the D65 white (Apple, CSS Color 4).
    private static ((double X, double Y) R, (double X, double Y) G, (double X, double Y) B) DisplayP3Primaries =>
        ((0.680, 0.320), (0.265, 0.690), (0.150, 0.060));

    /// <summary>Display P3's RGB→XYZ matrix adapted to D50, like the colorants of an ICC profile.</summary>
    public static ColorMatrix DisplayP3ToD50 { get; } =
        ColorMatrix.TryRgbToD50(DisplayP3Primaries.R, DisplayP3Primaries.G, DisplayP3Primaries.B, ColorMatrix.D65)
        ?? throw new InvalidOperationException("The Display P3 matrix is singular.");

    // ICC colorants are s15Fixed16 numbers computed by different tools; real Display P3 and sRGB profiles stay within
    // about 1e-3 of the nominal matrices, while the nearest other common spaces (Adobe RGB, BT.2020) differ by > 0.05.
    private const double ClassifyTolerance = 4e-3;

    /// <summary>The color space whose D50-adapted colorants match <paramref name="colorants"/>, or null.</summary>
    public static HdrColorSpace? Classify(ColorMatrix colorants) =>
        Close(colorants, ColorMatrix.SrgbToD50) ? HdrColorSpace.LinearSrgb
        : Close(colorants, DisplayP3ToD50) ? HdrColorSpace.LinearDisplayP3
        : null;

    /// <summary>The linear-light matrix from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public static ColorMatrix Conversion(HdrColorSpace source, HdrColorSpace target) =>
        source == target ? ColorMatrix.Diagonal(1, 1, 1) : Inverse(target) * ToD50(source);

    private static ColorMatrix ToD50(HdrColorSpace space) => space == HdrColorSpace.LinearSrgb ? ColorMatrix.SrgbToD50 : DisplayP3ToD50;

    private static ColorMatrix Inverse(HdrColorSpace space) =>
        space == HdrColorSpace.LinearSrgb ? ColorMatrix.D50ToSrgb : DisplayP3ToD50.Invert()!.Value;

    private static bool Close(ColorMatrix a, ColorMatrix b) =>
        Math.Abs(a.M00 - b.M00) <= ClassifyTolerance && Math.Abs(a.M01 - b.M01) <= ClassifyTolerance && Math.Abs(a.M02 - b.M02) <= ClassifyTolerance
        && Math.Abs(a.M10 - b.M10) <= ClassifyTolerance && Math.Abs(a.M11 - b.M11) <= ClassifyTolerance && Math.Abs(a.M12 - b.M12) <= ClassifyTolerance
        && Math.Abs(a.M20 - b.M20) <= ClassifyTolerance && Math.Abs(a.M21 - b.M21) <= ClassifyTolerance && Math.Abs(a.M22 - b.M22) <= ClassifyTolerance;
}
