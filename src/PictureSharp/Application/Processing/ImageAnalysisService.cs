using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Application.Processing;

/// <summary>Read-only analyses of an image; they never change pixels.</summary>
internal static class ImageAnalysisService
{
    public static ImageHistogram GetHistogram(Image image, HistogramOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= HistogramOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        Rectangle region = options.Region ?? new Rectangle(Point.Empty, image.Size);
        Image.CheckRectangle(region, image.Width, image.Height, nameof(options));

        long[] counts = new long[Histogram.Length];
        long pixels = 0;
        bool exclude = options.Transparency == HistogramTransparency.Exclude;
        int frames = options.AllFrames ? image.FrameCount : 1;
        for (int f = 0; f < frames; f++)
        {
            ImageFrame frame = image.Frames[f];
            if (region.Width == frame.Width)
            {
                ReadOnlySpan<Rgba32> pixelsInRegion = frame.GetPixelSpan().Slice(region.Y * frame.Width, region.Height * frame.Width);
                pixels += Histogram.Accumulate(pixelsInRegion, counts, exclude, cancellationToken);

                continue;
            }

            for (int y = region.Y; y < region.Bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pixels += Histogram.Accumulate(frame.GetRowSpan(y).Slice(region.X, region.Width), counts, exclude, cancellationToken);
            }
        }

        return new ImageHistogram(counts, pixels);
    }

    public static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= NegativeOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        return ImageProcessingContext.AnalyzeNegative(image, options, cancellationToken);
    }

    public static Rectangle? FindTrimBounds(Image image, AutoCropOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return TrimBounds.Find(image.Frames, image.Width, image.Height, options ?? AutoCropOptions.Default, cancellationToken);
    }
}
