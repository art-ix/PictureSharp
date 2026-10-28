namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Histogram-driven tone tables: Pillow's <c>ImageOps.autocontrast</c> and <c>ImageOps.equalize</c> on 256 counts per
/// channel, and their extensions to 65536 counts for 16-bit images (<c>AutoTone.Wide.cs</c>).
/// </summary>
internal static partial class AutoTone
{
    /// <summary>
    /// Fills the red, green and blue parts of <paramref name="lut"/> from <paramref name="counts"/> (the layout of
    /// <see cref="Histogram"/>). With <paramref name="preserveTone"/> one table from the luma counts serves all three.
    /// </summary>
    public static void AutoContrast(Span<byte> lut, ReadOnlySpan<long> counts, double cutoffLow, double cutoffHigh, bool preserveTone)
    {
        Span<double> h = stackalloc double[256];
        for (int c = 0; c < 3; c++)
        {
            int layer = preserveTone ? 4 : c;
            ReadOnlySpan<long> source = counts.Slice(layer * 256, 256);
            for (int i = 0; i < 256; i++)
            {
                h[i] = source[i];
            }

            AutoContrastLayer(lut.Slice(c * 256, 256), h, cutoffLow, cutoffHigh);
        }
    }

    // port: Pillow 10.2.0 src/PIL/ImageOps.py autocontrast (one histogram layer, without ignore and mask)
    private static void AutoContrastLayer(Span<byte> lut, Span<double> h, double cutoffLow, double cutoffHigh)
    {
        if (ContrastRange(h, cutoffLow, cutoffHigh) is not (int low, int high))
        {
            Identity(lut);
            return;
        }

        double scale = 255.0 / (high - low);
        double offset = -low * scale;
        for (int ix = 0; ix < 256; ix++)
        {
            lut[ix] = (byte)Math.Clamp((int)((ix * scale) + offset), 0, 255);
        }
    }

    /// <summary>
    /// The lowest and highest values left after cutting <paramref name="cutoffLow"/> and <paramref name="cutoffHigh"/>
    /// percent of the counts <paramref name="h"/> (one per value, changed in place), or null when they do not span a range.
    /// </summary>
    private static (int Low, int High)? ContrastRange(Span<double> h, double cutoffLow, double cutoffHigh)
    {
        int last = h.Length - 1;
        if (cutoffLow != 0 || cutoffHigh != 0)
        {
            double n = 0;
            for (int ix = 0; ix <= last; ix++)
            {
                n += h[ix];
            }

            // Counts become fractional once a fractional cut is subtracted, as in Python.
            double cut = PyFloorDivide(n * cutoffLow, 100);
            for (int lo = 0; lo <= last; lo++)
            {
                if (cut > h[lo])
                {
                    cut -= h[lo];
                    h[lo] = 0;
                }
                else
                {
                    h[lo] -= cut;
                    cut = 0;
                }

                if (cut <= 0)
                {
                    break;
                }
            }

            cut = PyFloorDivide(n * cutoffHigh, 100);
            for (int hi = last; hi >= 0; hi--)
            {
                if (cut > h[hi])
                {
                    cut -= h[hi];
                    h[hi] = 0;
                }
                else
                {
                    h[hi] -= cut;
                    cut = 0;
                }

                if (cut <= 0)
                {
                    break;
                }
            }
        }

        // Python's for loops leave the variable at the last value when nothing breaks.
        int low = last;
        for (int i = 0; i <= last; i++)
        {
            if (h[i] != 0)
            {
                low = i;
                break;
            }
        }

        int high = 0;
        for (int i = last; i >= 0; i--)
        {
            if (h[i] != 0)
            {
                high = i;
                break;
            }
        }

        return high <= low ? null : (low, high);
    }

    /// <summary>Fills the red, green and blue parts of <paramref name="lut"/> from <paramref name="counts"/>, each channel equalized separately.</summary>
    public static void Equalize(Span<byte> lut, ReadOnlySpan<long> counts)
    {
        for (int c = 0; c < 3; c++)
        {
            EqualizeLayer(lut.Slice(c * 256, 256), counts.Slice(c * 256, 256));
        }
    }

    // port: Pillow 10.2.0 src/PIL/ImageOps.py equalize (one histogram layer, without mask); ImagingPoint clips to 0–255
    private static void EqualizeLayer(Span<byte> lut, ReadOnlySpan<long> h)
    {
        long total = 0;
        long last = 0;
        int nonZero = 0;
        for (int i = 0; i < 256; i++)
        {
            if (h[i] != 0)
            {
                total += h[i];
                last = h[i];
                nonZero++;
            }
        }

        long step = nonZero <= 1 ? 0 : (total - last) / 255;
        if (step == 0)
        {
            Identity(lut);
            return;
        }

        long n = step / 2;
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)Math.Min(n / step, 255);
            n += h[i];
        }
    }

    private static void Identity(Span<byte> lut)
    {
        for (int i = 0; i < 256; i++)
        {
            lut[i] = (byte)i;
        }
    }

    // Python's float `a // b` for b > 0: floor of (a − fmod(a, b)) / b, snapped to the nearest integer as Python does.
    private static double PyFloorDivide(double a, double b)
    {
        double mod = a % b;
        double div = (a - mod) / b;
        if (mod < 0)
        {
            div -= 1;
        }

        double floor = Math.Floor(div);
        return div - floor > 0.5 ? floor + 1 : floor;
    }
}
