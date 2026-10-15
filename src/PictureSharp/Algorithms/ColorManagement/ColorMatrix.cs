namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>A row-major 3×3 matrix in double precision for RGB/XYZ conversions.</summary>
internal readonly record struct ColorMatrix(
    double M00, double M01, double M02,
    double M10, double M11, double M12,
    double M20, double M21, double M22)
{
    /// <summary>The ICC profile connection space illuminant (D50) as XYZ, ICC.1 §7.2.16.</summary>
    public static (double X, double Y, double Z) D50 => (0.9642, 1.0, 0.8249);

    /// <summary>The D65 white point used by sRGB, as xy.</summary>
    public static (double X, double Y) D65 => (0.3127, 0.3290);

    /// <summary>sRGB primaries (ITU-R BT.709) as xy: red, green, blue.</summary>
    public static ((double X, double Y) R, (double X, double Y) G, (double X, double Y) B) SrgbPrimaries =>
        ((0.64, 0.33), (0.30, 0.60), (0.15, 0.06));

    // Bradford cone response matrix (the constants LittleCMS also uses).
    private static ColorMatrix Bradford => new(
        0.8951, 0.2664, -0.1614,
        -0.7502, 1.7135, 0.0367,
        0.0389, -0.0685, 1.0296);

    /// <summary>The sRGB primaries' RGB→XYZ matrix adapted to D50, as in LittleCMS's built-in sRGB profile.</summary>
    public static ColorMatrix SrgbToD50 { get; } = RgbToD50(SrgbPrimaries.R, SrgbPrimaries.G, SrgbPrimaries.B, D65);

    /// <summary>The XYZ (D50)→linear sRGB matrix.</summary>
    public static ColorMatrix D50ToSrgb { get; } = SrgbToD50.Invert()
        ?? throw new InvalidOperationException("The sRGB matrix is singular.");

    public static ColorMatrix operator *(ColorMatrix a, ColorMatrix b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

    /// <summary>A matrix with the given columns.</summary>
    public static ColorMatrix FromColumns((double X, double Y, double Z) c0, (double X, double Y, double Z) c1, (double X, double Y, double Z) c2) =>
        new(c0.X, c1.X, c2.X, c0.Y, c1.Y, c2.Y, c0.Z, c1.Z, c2.Z);

    public static ColorMatrix Diagonal(double a, double b, double c) => new(a, 0, 0, 0, b, 0, 0, 0, c);

    public (double X, double Y, double Z) Transform((double X, double Y, double Z) v) => (
        (M00 * v.X) + (M01 * v.Y) + (M02 * v.Z),
        (M10 * v.X) + (M11 * v.Y) + (M12 * v.Z),
        (M20 * v.X) + (M21 * v.Y) + (M22 * v.Z));

    /// <summary>The inverse, or null for a singular or non-finite matrix.</summary>
    public ColorMatrix? Invert()
    {
        double c00 = (M11 * M22) - (M12 * M21);
        double c01 = (M12 * M20) - (M10 * M22);
        double c02 = (M10 * M21) - (M11 * M20);
        double det = (M00 * c00) + (M01 * c01) + (M02 * c02);
        if (!double.IsFinite(det) || Math.Abs(det) < 1e-12)
        {
            return null;
        }

        double inv = 1 / det;
        return new(
            c00 * inv, ((M02 * M21) - (M01 * M22)) * inv, ((M01 * M12) - (M02 * M11)) * inv,
            c01 * inv, ((M00 * M22) - (M02 * M20)) * inv, ((M02 * M10) - (M00 * M12)) * inv,
            c02 * inv, ((M01 * M20) - (M00 * M21)) * inv, ((M00 * M11) - (M01 * M10)) * inv);
    }

    /// <summary>XYZ with Y = 1 of chromaticity <paramref name="xy"/>.</summary>
    public static (double X, double Y, double Z) FromXy((double X, double Y) xy) => (xy.X / xy.Y, 1.0, (1.0 - xy.X - xy.Y) / xy.Y);

    /// <summary>The Bradford chromatic adaptation from white <paramref name="source"/> to <paramref name="target"/> (XYZ).</summary>
    // von Kries adaptation in the Bradford cone space, as in ICC.1:2022 Annex E.
    public static ColorMatrix? Adaptation((double X, double Y, double Z) source, (double X, double Y, double Z) target)
    {
        ColorMatrix cone = Bradford;
        (double sx, double sy, double sz) = cone.Transform(source);
        (double tx, double ty, double tz) = cone.Transform(target);
        if (cone.Invert() is not { } inverse || sx == 0 || sy == 0 || sz == 0)
        {
            return null;
        }

        return inverse * Diagonal(tx / sx, ty / sy, tz / sz) * cone;
    }

    /// <summary>
    /// The RGB→XYZ matrix of the given primaries and white point (xy), adapted to D50 with Bradford, or null when the
    /// chromaticities are degenerate.
    /// </summary>
    // Primaries scaled so they sum to the white point (SMPTE RP 177), then adapted to the D50 PCS illuminant.
    public static ColorMatrix? TryRgbToD50((double X, double Y) red, (double X, double Y) green, (double X, double Y) blue, (double X, double Y) white)
    {
        if (red.Y <= 0 || green.Y <= 0 || blue.Y <= 0 || white.Y <= 0)
        {
            return null;
        }

        ColorMatrix primaries = FromColumns(FromXy(red), FromXy(green), FromXy(blue));
        (double X, double Y, double Z) whiteXyz = FromXy(white);
        if (primaries.Invert() is not { } inverse)
        {
            return null;
        }

        (double sr, double sg, double sb) = inverse.Transform(whiteXyz);
        ColorMatrix native = primaries * Diagonal(sr, sg, sb);
        return Adaptation(whiteXyz, D50) is { } adapt ? adapt * native : null;
    }

    private static ColorMatrix RgbToD50((double X, double Y) red, (double X, double Y) green, (double X, double Y) blue, (double X, double Y) white) =>
        TryRgbToD50(red, green, blue, white) ?? throw new InvalidOperationException("Degenerate primaries.");
}
