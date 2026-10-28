namespace PictureSharp.Algorithms.Processing;

/// <summary>Normalized (0–1) sample-level settings in the codes of a storage depth.</summary>
internal static class NormalizedLevel
{
    // Float stores v / 255f within 6e-8 of the range from the exact value; 2.5e-7 catches that with a margin. At 8 bits
    // this is 6e-5 of a level, so other settings stay unrounded; at 16 bits snapping moves a value by at most 0.002 codes.
    private const double SnapTolerance = 2.5e-7;

    /// <summary>
    /// <paramref name="value"/> in codes of 0–<paramref name="max"/>. A value within float rounding of a multiple of
    /// 1/256 code is snapped to it, so a setting migrated from a level v as <c>v / 255f</c> gives exactly v on 8-bit
    /// images and v · 257 on 16-bit ones (including fractional levels such as 10.5 or 3.25).
    /// </summary>
    public static double ToCodes(float value, int max)
    {
        double codes = value * (double)max;
        double snapped = Math.Round(codes * 256) / 256;
        return Math.Abs(codes - snapped) <= max * SnapTolerance ? snapped : codes;
    }

    /// <summary>The largest integer code that a threshold "above <paramref name="value"/>" admits as margin.</summary>
    public static int Threshold(float value, int max) => (int)Math.Floor(ToCodes(value, max));

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN, infinite or outside 0–1.</exception>
    public static float Check(float value, string paramName) =>
        value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(paramName, value, "Expected a normalized value between 0 and 1.");
}
