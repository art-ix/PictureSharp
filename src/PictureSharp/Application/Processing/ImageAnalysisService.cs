using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Application.Processing;

/// <summary>Read-only analyses of an image; they never change pixels.</summary>
internal static class ImageAnalysisService
{
    private const int RowsPerBlock = 64;

    public static ImageHistogram GetHistogram(Image image, HistogramOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= HistogramOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        Rectangle region = options.Region ?? new Rectangle(Point.Empty, image.Size);
        if (region.Width <= 0 || region.Height <= 0 || region.X < 0 || region.Y < 0 ||
            region.Right > image.Width || region.Bottom > image.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(options), region, "The histogram region must be non-empty and inside the image.");
        }

        long[] counts = new long[Histogram.Length];
        long pixels = 0;
        bool exclude = options.Transparency == HistogramTransparency.Exclude;
        int frames = options.AllFrames ? image.FrameCount : 1;
        for (int f = 0; f < frames; f++)
        {
            ImageFrame frame = image.Frames[f];
            if (region.Width == frame.Width)
            {
                // Full rows are contiguous: count blocks of rows, checking cancellation between them.
                ReadOnlySpan<Rgba32> all = frame.GetPixelSpan();
                for (int y = region.Y; y < region.Bottom; y += RowsPerBlock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int rows = Math.Min(RowsPerBlock, region.Bottom - y);
                    pixels += Histogram.Accumulate(all.Slice(y * frame.Width, rows * frame.Width), counts, exclude);
                }

                continue;
            }

            for (int y = region.Y; y < region.Bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pixels += Histogram.Accumulate(frame.GetRowSpan(y).Slice(region.X, region.Width), counts, exclude);
            }
        }

        return new ImageHistogram(counts, pixels);
    }

    public static Rectangle? FindTrimBounds(Image image, AutoCropOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return TrimBounds.Find(image.Frames, image.Width, image.Height, options ?? AutoCropOptions.Default, cancellationToken);
    }
}
