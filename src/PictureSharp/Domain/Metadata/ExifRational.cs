namespace PictureSharp;

/// <summary>An exact unsigned EXIF fraction.</summary>
public readonly record struct ExifRational
{
    /// <summary>Creates a fraction with a nonzero denominator.</summary>
    public ExifRational(uint numerator, uint denominator)
    {
        ArgumentOutOfRangeException.ThrowIfZero(denominator);
        Numerator = numerator;
        Denominator = denominator;
    }

    /// <summary>The numerator.</summary>
    public uint Numerator { get; }
    /// <summary>The denominator; zero only for the default, invalid value.</summary>
    public uint Denominator { get; }
    /// <summary>Returns the floating-point value.</summary>
    public double ToDouble() => (double)Numerator / Denominator;
}
