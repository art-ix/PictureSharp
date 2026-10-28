using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;
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
                lut[(c * 256) + v] = (byte)(int)Math.Floor((Print(t, gamma, print, gain) * 255) + 0.5);
            }
        }
    }

    /// <summary>
    /// Fills <paramref name="lut"/> (R, G, B × 65536) for 16-bit values as <see cref="Lut(Span{byte}, NegativeCalibration, NegativePrintOptions)"/>
    /// does, each print evaluated in double precision and rounded once to 16 bits.
    /// </summary>
    public static void Lut(Span<ushort> lut, NegativeCalibration calibration, NegativePrintOptions print)
    {
        const int N = ushort.MaxValue + 1;
        double gain = Math.Pow(2, print.Exposure);
        ushort[] result = new ushort[3 * N];
        // 196608 evaluations of three powers: channels and value ranges in parallel, each entry independent.
        Parallel.For(0, 3 * 16, Parallelism.Options(-1, CancellationToken.None), part =>
        {
            int c = part / 16, start = (part % 16) * (N / 16);
            double filmBase = calibration.FilmBase[c], range = calibration.DensityRange[c], gamma = calibration.MidtoneGamma[c];
            for (int v = start; v < start + (N / 16); v++)
            {
                double t = Math.Clamp(NegativeCalibrator.Density<Rgba64>(filmBase, v) / range, 0, Headroom);
                result[(c * N) + v] = (ushort)(int)Math.Floor((Print(t, gamma, print, gain) * 65535) + 0.5);
            }
        });
        result.CopyTo(lut);
    }

    /// <summary>
    /// Prints <paramref name="source"/> into <paramref name="destination"/> as <see cref="Lut(Span{byte}, NegativeCalibration, NegativePrintOptions)"/>
    /// does, with the film base of each channel scaled by <c>10^(X · x + Y · y + C)</c> at the center
    /// (x + 0.5, y + 0.5) of every pixel, which evens out the backlight. The print of t is tabulated at 4096 steps on
    /// 8-bit images and at 65536 on 16-bit ones, and interpolated linearly.
    /// </summary>
    public static void PrintLit<TPixel>(TPixel[] source, TPixel[] destination, int width, int height, NegativeCalibration calibration,
        NegativePrintOptions print, ChannelValues slopeX, ChannelValues slopeY, ChannelValues offset, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            PrintLit(Unsafe.As<Rgba32[]>(source), Unsafe.As<Rgba32[]>(destination), width, height, calibration, print, slopeX, slopeY, offset,
                cancellationToken);
        }
        else
        {
            PrintLitWide(Unsafe.As<Rgba64[]>(source), Unsafe.As<Rgba64[]>(destination), width, height, calibration, print, slopeX, slopeY,
                offset, cancellationToken);
        }
    }

    private static void PrintLit(Rgba32[] source, Rgba32[] destination, int width, int height, NegativeCalibration calibration,
        NegativePrintOptions print, ChannelValues slopeX, ChannelValues slopeY, ChannelValues offset, CancellationToken cancellationToken)
    {
        const int Steps = 4096;
        double gain = Math.Pow(2, print.Exposure);
        bool gray = calibration.FilmType == FilmType.BlackAndWhiteNegative;
        // d = log10(base) − log10(L) + shift, t = d / range: per channel the value's term and the print of t.
        double[] density = new double[3 * 256];
        double[] curve = new double[3 * (Steps + 2)];
        for (int c = 0; c < 3; c++)
        {
            double logBase = Math.Log10(calibration.FilmBase[c]);
            for (int v = 0; v < 256; v++)
            {
                density[(c * 256) + v] = logBase - Math.Log10(Math.Max(SrgbTransfer.Decode((byte)v), NegativeCalibrator.DensityFloor));
            }

            for (int i = 0; i <= Steps + 1; i++)
            {
                curve[(c * (Steps + 2)) + i] = Print(Math.Min(Headroom, i * Headroom / Steps), calibration.MidtoneGamma[c], print, gain);
            }
        }

        Parallelism.ForBands(height, 16, (long)width * height * 8, cancellationToken, (start, end) =>
        {
            Span<double> scale = stackalloc double[3];
            Span<double> row = stackalloc double[3];
            for (int c = 0; c < 3; c++)
            {
                scale[c] = Steps / (Headroom * calibration.DensityRange[c]);
            }

            for (int y = start; y < end; y++)
            {
                for (int c = 0; c < 3; c++)
                {
                    row[c] = offset[c] + (slopeY[c] * (y + 0.5));
                }

                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    Rgba32 p = source[i];
                    byte luma = gray ? (byte)GrayWorld.Luma(p) : (byte)0;
                    Span<byte> output = [0, 0, 0];
                    for (int c = 0; c < 3; c++)
                    {
                        int v = gray ? luma : c switch { 0 => p.R, 1 => p.G, _ => p.B };
                        double d = density[(c * 256) + v] + row[c] + (slopeX[c] * (x + 0.5));
                        double position = Math.Clamp(d * scale[c], 0, Steps);
                        int at = (int)position;
                        double f = position - at;
                        int k = (c * (Steps + 2)) + at;
                        double s = curve[k] + (f * (curve[k + 1] - curve[k]));
                        output[c] = (byte)(int)Math.Floor((s * 255) + 0.5);
                    }

                    destination[i] = new Rgba32(output[0], output[1], output[2], p.A);
                }
            }
        });
    }

    // PrintLit on 16-bit values: the density of every value from one table of log10 of its linear light.
    private static void PrintLitWide(Rgba64[] source, Rgba64[] destination, int width, int height, NegativeCalibration calibration,
        NegativePrintOptions print, ChannelValues slopeX, ChannelValues slopeY, ChannelValues offset, CancellationToken cancellationToken)
    {
        const int Steps = 65536, N = ushort.MaxValue + 1;
        double gain = Math.Pow(2, print.Exposure);
        bool gray = calibration.FilmType == FilmType.BlackAndWhiteNegative;
        double[] logLinear = new double[N];
        double[] curve = new double[3 * (Steps + 2)];
        Parallel.For(0, 4, Parallelism.Options(-1, cancellationToken), part =>
        {
            if (part == 3)
            {
                for (int v = 0; v < N; v++)
                {
                    logLinear[v] = Math.Log10(Math.Max(SrgbTransfer.Decode16((ushort)v), NegativeCalibrator.DensityFloor));
                }

                return;
            }

            for (int i = 0; i <= Steps + 1; i++)
            {
                curve[(part * (Steps + 2)) + i] = Print(Math.Min(Headroom, i * Headroom / Steps), calibration.MidtoneGamma[part], print, gain);
            }
        });

        Parallelism.ForBands(height, 16, (long)width * height * 8, cancellationToken, (start, end) =>
        {
            Span<double> scale = stackalloc double[3];
            Span<double> logBase = stackalloc double[3];
            Span<double> row = stackalloc double[3];
            Span<ushort> output = stackalloc ushort[3];
            for (int c = 0; c < 3; c++)
            {
                scale[c] = Steps / (Headroom * calibration.DensityRange[c]);
                logBase[c] = Math.Log10(calibration.FilmBase[c]);
            }

            for (int y = start; y < end; y++)
            {
                for (int c = 0; c < 3; c++)
                {
                    row[c] = logBase[c] + offset[c] + (slopeY[c] * (y + 0.5));
                }

                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    Rgba64 p = source[i];
                    int luma = gray ? GrayWorld.Luma(p) : 0;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = gray ? luma : c switch { 0 => p.R, 1 => p.G, _ => p.B };
                        double d = row[c] - logLinear[v] + (slopeX[c] * (x + 0.5));
                        double position = Math.Clamp(d * scale[c], 0, Steps);
                        int at = (int)position;
                        double f = position - at;
                        int k = (c * (Steps + 2)) + at;
                        double s = curve[k] + (f * (curve[k + 1] - curve[k]));
                        output[c] = (ushort)(int)Math.Floor((s * 65535) + 0.5);
                    }

                    destination[i] = new Rgba64(output[0], output[1], output[2], p.A);
                }
            }
        });
    }

    // The printed, encoded value (0–1) of share t: t^γ, 10^(Contrast · (t^γ − 1)) · gain, sRGB, the S curve.
    private static double Print(double t, double gamma, NegativePrintOptions print, double gain)
    {
        double linear = Math.Pow(10, print.Contrast * (Math.Pow(t, gamma) - 1)) * gain;
        double s = SrgbTransfer.EncodeExact(linear);
        s += print.Curve * s * (1 - s) * ((2 * s) - 1);
        return Math.Clamp(s, 0, 1);
    }
}
