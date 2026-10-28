using System.Drawing;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class TrimBounds
{
    private static Rectangle? FindRgba64(IEnumerable<ImageFrame> frames, int width, int height, AutoCropOptions autoCrop,
        CancellationToken cancellationToken, bool vectorize)
    {
        var options = new Criteria64(autoCrop.Mode == AutoCropMode.Alpha,
            (ushort)NormalizedLevel.Threshold(autoCrop.AlphaThresholdNormalized, ushort.MaxValue),
            (ushort)NormalizedLevel.Threshold(autoCrop.ToleranceNormalized, ushort.MaxValue));
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        Rgba64 background = default;
        bool first = true;
        foreach (ImageFrame frame in frames)
        {
            if (first)
            {
                background = autoCrop.Background is { } color ? color : frame.GetRowSpan<Rgba64>(0)[0];
                first = false;
            }

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<Rgba64> row = frame.GetRowSpan<Rgba64>(y);
                int x0 = FirstContent(row, options, background, vectorize);
                if (x0 < 0)
                {
                    continue;
                }

                // Only the part outside the current box can widen it.
                int x1 = LastContent(row, Math.Max(x0, right + 1), options, background, vectorize);
                left = Math.Min(left, x0);
                right = Math.Max(right, Math.Max(x0, x1));
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return Bounds(left, top, right, bottom, width, height, autoCrop.Padding);
    }

    private readonly record struct Criteria64(bool AlphaMode, ushort AlphaThreshold, ushort Tolerance);

    private static int FirstContent(ReadOnlySpan<Rgba64> row, Criteria64 options, Rgba64 background, bool vectorize)
    {
        int start = 0;
        if (vectorize)
        {
            int found = FirstContentSimd(row, options, background, out start);
            if (found >= 0)
            {
                return found;
            }
        }

        for (int x = start; x < row.Length; x++)
        {
            if (IsContent(row[x], options, background))
            {
                return x;
            }
        }

        return -1;
    }

    /// <summary>The last content pixel at or after <paramref name="from"/>, or -1.</summary>
    private static int LastContent(ReadOnlySpan<Rgba64> row, int from, Criteria64 options, Rgba64 background, bool vectorize)
    {
        int end = row.Length;
        if (vectorize)
        {
            int found = LastContentSimd(row, from, options, background, out end);
            if (found >= 0)
            {
                return found;
            }
        }

        for (int x = end - 1; x >= from; x--)
        {
            if (IsContent(row[x], options, background))
            {
                return x;
            }
        }

        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsContent(Rgba64 p, Criteria64 options, Rgba64 background)
    {
        if (options.AlphaMode)
        {
            return p.A > options.AlphaThreshold;
        }

        if (background.A == 0 && p.A == 0)
        {
            return false;
        }

        int difference = Math.Max(Math.Max(Math.Abs(p.R - background.R), Math.Abs(p.G - background.G)),
            Math.Max(Math.Abs(p.B - background.B), Math.Abs(p.A - background.A)));
        return difference > options.Tolerance;
    }
}
