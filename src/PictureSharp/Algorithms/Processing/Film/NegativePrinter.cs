using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Turns a calibrated negative into a positive: every channel value maps through its own curve, so the whole
/// inversion is one lookup table, computed in double precision. Mirrored by tests/assets/photo_adjust_reference.py
/// (invert_negative).
/// </summary>
internal static class NegativePrinter
{
    // A little headroom past the density range, so the densest highlights are not all the same white before the S curve.
    private const double Headroom = 1.05;

    /// <summary>
    /// Fills <paramref name="lut"/> (R, G, B × 256): <c>t = clamp(d / range, 0, 1.05)^γ</c>,
    /// <c>L = 10^(Contrast · (t − 1)) · 2^Exposure</c>, <c>s = sRGB(L)</c>, then <c>s + Curve · s(1 − s)(2s − 1)</c>,
    /// rounded half up; d is the density above the film base.
    /// </summary>
    public static void Lut(Span<byte> lut, NegativeCalibration calibration, NegativePrintOptions print)
    {
        double gain = Math.Pow(2, print.Exposure);
        for (int c = 0; c < 3; c++)
        {
            double filmBase = calibration.FilmBase[c], range = calibration.DensityRange[c], gamma = calibration.MidtoneGamma[c];
            for (int v = 0; v < 256; v++)
            {
                double t = Math.Clamp(NegativeCalibrator.Density(filmBase, v) / range, 0, Headroom);
                double linear = Math.Pow(10, print.Contrast * (Math.Pow(t, gamma) - 1)) * gain;
                double s = SrgbTransfer.EncodeExact(linear);
                s += print.Curve * s * (1 - s) * ((2 * s) - 1);
                lut[(c * 256) + v] = (byte)(int)Math.Floor((Math.Clamp(s, 0, 1) * 255) + 0.5);
            }
        }
    }
}
