using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Measures a photographed negative: the film base, each channel's density range and the midtone gammas, from integer
/// histograms of a regular sample of the measured region, so the result does not depend on threads. Mirrored by
/// tests/assets/photo_adjust_reference.py (analyze_negative).
/// </summary>
internal static class NegativeCalibrator
{
    // At most about this many pixels are sampled; every k-th row and column, k fixed by the region's area.
    private const double SampleTarget = 1_000_000;
    // Without a film-base region, the base is this percentile of each channel: the lightest pixels, past stray
    // highlights such as a scratch.
    private const float BasePercentile = 99.7f;
    private const float ClippedWarning = 0.01f, LowRangeWarning = 0.3f;

    /// <summary>The linear light below which samples count as this floor in the density.</summary>
    public const double DensityFloor = 1.0 / 1024;

    /// <summary>Measures <paramref name="pixels"/> (one frame, stored values) with <paramref name="options"/>.</summary>
    public static NegativeAnalysis Analyze(ReadOnlySpan<Rgba32> pixels, int width, int height, NegativeOptions options,
        CancellationToken cancellationToken)
    {
        Rectangle region = options.Region ?? new Rectangle(0, 0, width, height);
        bool gray = options.FilmType == FilmType.BlackAndWhiteNegative;
        int step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)region.Width * region.Height / SampleTarget)));
        long[] candidates = new long[3 * 256];
        long[] all = new long[3 * 256];
        Span<long> clipped = stackalloc long[3];
        long opaque = 0, usable = 0;
        for (int y = region.Y; y < region.Bottom; y += step)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<Rgba32> row = pixels.Slice(y * width, width);
            for (int x = region.X; x < region.Right; x += step)
            {
                Rgba32 p = row[x];
                if (p.A == 0)
                {
                    continue;
                }

                opaque++;
                bool candidate = true;
                for (int c = 0; c < 3; c++)
                {
                    int v = Value(p, c, gray);
                    all[(c * 256) + v]++;
                    if (v is <= 2 or >= 253)
                    {
                        clipped[c]++;
                        candidate = false;
                    }
                }

                if (candidate)
                {
                    usable++;
                    for (int c = 0; c < 3; c++)
                    {
                        candidates[(c * 256) + Value(p, c, gray)]++;
                    }
                }
            }
        }

        // An image whose every pixel is clipped is still measured, from all of them.
        long[] counts = usable > 0 ? candidates : all;
        Span<double> filmBase = stackalloc double[3];
        if (options.FilmBaseRegion is { } baseRegion)
        {
            MedianOfRegion(pixels, width, baseRegion, gray, filmBase, cancellationToken);
        }
        else
        {
            for (int c = 0; c < 3; c++)
            {
                filmBase[c] = SrgbTransfer.Decode((byte)Percentile(counts.AsSpan(c * 256, 256), BasePercentile));
            }
        }

        Span<double> range = stackalloc double[3];
        Span<double> median = stackalloc double[3];
        for (int c = 0; c < 3; c++)
        {
            ReadOnlySpan<long> channel = counts.AsSpan(c * 256, 256);
            filmBase[c] = Math.Clamp(filmBase[c], SrgbTransfer.Decode(1), 1.0);
            // The densest pixels have the lowest values.
            range[c] = Math.Clamp(Density(filmBase[c], Percentile(channel, 100f - options.HighlightPercentile)), 0.05, 6.0);
            median[c] = Math.Clamp(Density(filmBase[c], Percentile(channel, 50f)) / range[c], 0.01, 0.99);
        }

        // Each channel's median moves towards the geometric mean of the medians: t^γ = target exactly at balance 1.
        double target = Math.Exp((Math.Log(median[0]) + Math.Log(median[1]) + Math.Log(median[2])) / 3);
        Span<double> gamma = stackalloc double[3];
        for (int c = 0; c < 3; c++)
        {
            gamma[c] = Math.Clamp(Math.Pow(Math.Log(target) / Math.Log(median[c]), options.MidtoneBalance), 0.2, 5.0);
        }

        var calibration = new NegativeCalibration
        {
            FilmType = options.FilmType,
            FilmBase = new((float)filmBase[0], (float)filmBase[1], (float)filmBase[2]),
            DensityRange = new((float)range[0], (float)range[1], (float)range[2]),
            MidtoneGamma = new((float)gamma[0], (float)gamma[1], (float)gamma[2]),
        };
        float share = opaque == 0 ? 0f : 1f / opaque;
        var clippedFraction = new ChannelValues(clipped[0] * share, clipped[1] * share, clipped[2] * share);
        NegativeWarnings warnings = NegativeWarnings.None;
        if (clippedFraction.Red > ClippedWarning || clippedFraction.Green > ClippedWarning || clippedFraction.Blue > ClippedWarning)
        {
            warnings |= NegativeWarnings.ChannelClipped;
        }

        if (options.FilmBaseRegion is null)
        {
            warnings |= NegativeWarnings.FilmBaseEstimated;
        }

        if (Math.Min(range[0], Math.Min(range[1], range[2])) < LowRangeWarning)
        {
            warnings |= NegativeWarnings.LowDensityRange;
        }

        return new NegativeAnalysis(calibration, clippedFraction, warnings);
    }

    /// <summary>The density above <paramref name="filmBase"/> of the encoded value <paramref name="value"/>.</summary>
    public static double Density(double filmBase, int value) =>
        Math.Log10(filmBase / Math.Max(SrgbTransfer.Decode((byte)value), DensityFloor));

    /// <summary>The value read by a black-and-white negative (the integer Rec. 709 luma) or the color channel.</summary>
    public static int Value(Rgba32 p, int channel, bool gray) =>
        gray ? GrayWorld.Luma(p) : channel switch { 0 => p.R, 1 => p.G, _ => p.B };

    /// <summary>The smallest value whose cumulative count reaches <c>ceil(percentile / 100 · n)</c>, at least the first.</summary>
    private static int Percentile(ReadOnlySpan<long> counts, float percentile)
    {
        long n = 0;
        foreach (long c in counts)
        {
            n += c;
        }

        long rank = Math.Clamp((long)Math.Ceiling(percentile / 100.0 * n), 1, Math.Max(1, n));
        long cumulative = 0;
        for (int v = 0; v < 256; v++)
        {
            cumulative += counts[v];
            if (cumulative >= rank)
            {
                return v;
            }
        }

        return 255;
    }

    private static void MedianOfRegion(ReadOnlySpan<Rgba32> pixels, int width, Rectangle region, bool gray, Span<double> filmBase,
        CancellationToken cancellationToken)
    {
        long[] counts = new long[3 * 256];
        for (int y = region.Y; y < region.Bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Rgba32 p in pixels.Slice((y * width) + region.X, region.Width))
            {
                if (p.A == 0)
                {
                    continue;
                }

                for (int c = 0; c < 3; c++)
                {
                    counts[(c * 256) + Value(p, c, gray)]++;
                }
            }
        }

        for (int c = 0; c < 3; c++)
        {
            filmBase[c] = SrgbTransfer.Decode((byte)Percentile(counts.AsSpan(c * 256, 256), 50f));
        }
    }
}
