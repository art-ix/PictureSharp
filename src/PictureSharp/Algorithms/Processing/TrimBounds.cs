using System.Drawing;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
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
        PixelType pixelType = frames.FirstOrDefault()?.PixelType ?? PixelType.Rgba32;
        var find = new FindOperation(frames, width, height, options, cancellationToken, vectorize);
        return PixelDispatch.Run<FindOperation, Rectangle?>(pixelType, ref find);
    }

    private readonly struct FindOperation(IEnumerable<ImageFrame> frames, int width, int height, AutoCropOptions options,
        CancellationToken cancellationToken, bool vectorize) : IPixelOperation<Rectangle?>
    {
        public Rectangle? Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            typeof(TPixel) == typeof(Rgba32)
                ? FindRgba32(frames, width, height, options, cancellationToken, vectorize)
                : FindScalar<TPixel>(frames, width, height, options, cancellationToken);
    }

    /// <summary>
    /// The scan at any depth. The normalized thresholds of <see cref="AutoCropOptions"/> become integer codes of the
    /// storage depth (an 8-bit level t / 255f is t · 257 at 16 bits), so an exactly widened image has the same bounds
    /// as the 8-bit original.
    /// </summary>
    private static Rectangle? FindScalar<TPixel>(IEnumerable<ImageFrame> frames, int width, int height, AutoCropOptions options,
        CancellationToken cancellationToken) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        int alphaThreshold = NormalizedLevel.Threshold(options.AlphaThresholdNormalized, TPixel.MaxValue);
        int tolerance = NormalizedLevel.Threshold(options.ToleranceNormalized, TPixel.MaxValue);
        bool alphaMode = options.Mode == AutoCropMode.Alpha;
        int left = width;
        int top = height;
        int right = -1;
        int bottom = -1;
        TPixel background = default;
        bool first = true;
        foreach (ImageFrame frame in frames)
        {
            TPixel[] pixels = frame.Buffer.As<TPixel>();
            if (first)
            {
                background = options.Background is { } color ? TPixel.FromRgba64(color) : pixels[0];
                first = false;
            }

            bool transparentBackground = TPixel.Alpha(background) == 0;
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<TPixel> row = pixels.AsSpan(y * width, width);
                for (int x = 0; x < row.Length; x++)
                {
                    TPixel p = row[x];
                    bool content = alphaMode
                        ? TPixel.Alpha(p) > alphaThreshold
                        : !(transparentBackground && TPixel.Alpha(p) == 0) && TPixel.MaxChannelDifference(p, background) > tolerance;
                    if (content)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }
        }

        return Bounds(left, top, right, bottom, width, height, options.Padding);
    }

    private static Rectangle? FindRgba32(IEnumerable<ImageFrame> frames, int width, int height, AutoCropOptions autoCrop,
        CancellationToken cancellationToken, bool vectorize)
    {
        var options = new Criteria(autoCrop.Mode == AutoCropMode.Alpha,
            (byte)NormalizedLevel.Threshold(autoCrop.AlphaThresholdNormalized, byte.MaxValue),
            (byte)NormalizedLevel.Threshold(autoCrop.ToleranceNormalized, byte.MaxValue));
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
                background = autoCrop.Background is { } color ? color.ToRgba32() : frame.PixelRow(0)[0];
                first = false;
            }

            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadOnlySpan<Rgba32> row = frame.PixelRow(y);
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

    /// <summary>The 8-bit thresholds of <see cref="AutoCropOptions"/>: content lies above them.</summary>
    private readonly record struct Criteria(bool AlphaMode, byte AlphaThreshold, byte Tolerance);

    private static Rectangle? Bounds(int left, int top, int right, int bottom, int width, int height, int padding)
    {
        if (right < 0)
        {
            return null;
        }

        int x = Math.Max(0, left - padding);
        int yTop = Math.Max(0, top - padding);
        int xEnd = (int)Math.Min(width, (long)right + 1 + padding);
        int yEnd = (int)Math.Min(height, (long)bottom + 1 + padding);
        return new Rectangle(x, yTop, xEnd - x, yEnd - yTop);
    }

    private static int FirstContent(ReadOnlySpan<Rgba32> row, Criteria options, Rgba32 background, bool vectorize)
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
    private static int LastContent(ReadOnlySpan<Rgba32> row, int from, Criteria options, Rgba32 background, bool vectorize)
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
    private static bool IsContent(Rgba32 p, Criteria options, Rgba32 background)
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
