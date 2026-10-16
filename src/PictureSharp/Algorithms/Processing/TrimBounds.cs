using System.Drawing;
using System.Runtime.CompilerServices;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// The bounding box of the content of frames: pixels that are not margin under <see cref="AutoCropOptions"/>
/// (tests/assets/graphics_reference.py trim_bounds).
/// </summary>
internal static partial class TrimBounds
{
    /// <summary>
    /// The union of the content boxes of <paramref name="frames"/>, expanded by the padding and clipped to the canvas,
    /// or <see langword="null"/> when no frame has content. <paramref name="vectorize"/> = false keeps to the scalar
    /// scan, which the vector scan equals (tests compare them).
    /// </summary>
    public static Rectangle? Find(IEnumerable<ImageFrame> frames, int width, int height, AutoCropOptions options, CancellationToken cancellationToken,
        bool vectorize = true)
    {
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        Rgba32 background = default;
        bool first = true;
        foreach (ImageFrame frame in frames)
        {
            if (first)
            {
                background = options.Background ?? frame.GetRowSpan(0)[0];
                first = false;
            }

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<Rgba32> row = frame.GetRowSpan(y);
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

        if (right < 0)
        {
            return null;
        }

        int padding = options.Padding;
        int x = Math.Max(0, left - padding);
        int yTop = Math.Max(0, top - padding);
        int xEnd = (int)Math.Min(width, (long)right + 1 + padding);
        int yEnd = (int)Math.Min(height, (long)bottom + 1 + padding);
        return new Rectangle(x, yTop, xEnd - x, yEnd - yTop);
    }

    private static int FirstContent(ReadOnlySpan<Rgba32> row, AutoCropOptions options, Rgba32 background, bool vectorize)
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
    private static int LastContent(ReadOnlySpan<Rgba32> row, int from, AutoCropOptions options, Rgba32 background, bool vectorize)
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
    private static bool IsContent(Rgba32 p, AutoCropOptions options, Rgba32 background)
    {
        if (options.Mode == AutoCropMode.Alpha)
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
