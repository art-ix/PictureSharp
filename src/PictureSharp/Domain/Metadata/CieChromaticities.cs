namespace PictureSharp;

/// <summary>
/// CIE 1931 xy chromaticities of a white point and the red, green and blue primaries, as stored by the PNG
/// <c>cHRM</c> chunk. Every coordinate must be in [0, 1] with y greater than 0, and the primaries must span a color
/// space that contains the white point.
/// </summary>
public readonly record struct CieChromaticities
{
    /// <summary>Creates chromaticities from the white point and primaries.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is outside [0, 1], a y is 0, or x + y exceeds 1.</exception>
    /// <exception cref="ArgumentException">The primaries are collinear, or the white point is outside their triangle.</exception>
    public CieChromaticities(float whiteX, float whiteY, float redX, float redY, float greenX, float greenY, float blueX, float blueY)
    {
        WhiteX = Check(whiteX, whiteY, nameof(whiteX));
        WhiteY = whiteY;
        RedX = Check(redX, redY, nameof(redX));
        RedY = redY;
        GreenX = Check(greenX, greenY, nameof(greenX));
        GreenY = greenY;
        BlueX = Check(blueX, blueY, nameof(blueX));
        BlueY = blueY;
        CheckGamut();
    }

    /// <summary>The chromaticities of sRGB (ITU-R BT.709 primaries, D65 white).</summary>
    public static CieChromaticities Srgb { get; } = new(0.3127f, 0.3290f, 0.64f, 0.33f, 0.30f, 0.60f, 0.15f, 0.06f);

    /// <summary>White point x.</summary>
    public float WhiteX { get; }

    /// <summary>White point y.</summary>
    public float WhiteY { get; }

    /// <summary>Red primary x.</summary>
    public float RedX { get; }

    /// <summary>Red primary y.</summary>
    public float RedY { get; }

    /// <summary>Green primary x.</summary>
    public float GreenX { get; }

    /// <summary>Green primary y.</summary>
    public float GreenY { get; }

    /// <summary>Blue primary x.</summary>
    public float BlueX { get; }

    /// <summary>Blue primary y.</summary>
    public float BlueY { get; }

    // The white point must be a positive mix of the primaries, i.e. inside their triangle in xy; this also rejects
    // collinear primaries, whose RGB-to-XYZ matrix is singular.
    private void CheckGamut()
    {
        double d = Cross(RedX, RedY, GreenX, GreenY, BlueX, BlueY);
        double r = Cross(WhiteX, WhiteY, GreenX, GreenY, BlueX, BlueY) / d;
        double g = Cross(RedX, RedY, WhiteX, WhiteY, BlueX, BlueY) / d;
        double b = Cross(RedX, RedY, GreenX, GreenY, WhiteX, WhiteY) / d;
        if (!(Math.Abs(d) > 1e-9 && r > 0 && g > 0 && b > 0))
            throw new ArgumentException("The primaries must form a triangle containing the white point.");

        static double Cross(double ax, double ay, double bx, double by, double cx, double cy) =>
            ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax));
    }

    private static float Check(float x, float y, string name) =>
        x is >= 0 and <= 1 && y is > 0 and <= 1 && x + y <= 1
            ? x
            : throw new ArgumentOutOfRangeException(name, $"Chromaticity ({x}, {y}) must have 0 ≤ x, 0 < y and x + y ≤ 1.");
}
