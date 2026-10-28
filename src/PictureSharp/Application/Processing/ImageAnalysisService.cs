using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Pixels;
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

        var count = new HistogramCount(image, region, options, cancellationToken);
        return PixelDispatch.Run<HistogramCount, ImageHistogram>(image.PixelType, ref count);
    }

    private readonly struct HistogramCount(Image image, Rectangle region, HistogramOptions options, CancellationToken cancellationToken)
        : IPixelOperation<ImageHistogram>
    {
        public ImageHistogram Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            int binCount = options.FullResolution ? TPixel.MaxValue + 1 : 256;
            long[] counts = new long[Histogram.Channels * binCount];
            long pixels = 0;
            bool exclude = options.Transparency == HistogramTransparency.Exclude;
            int frames = options.AllFrames ? image.FrameCount : 1;
            for (int f = 0; f < frames; f++)
            {
                ImageFrame frame = image.Frames[f];
                TPixel[] buffer = frame.Buffer.As<TPixel>();
                if (region.Width == frame.Width)
                {
                    ReadOnlySpan<TPixel> pixelsInRegion = buffer.AsSpan(region.Y * frame.Width, region.Height * frame.Width);
                    pixels += Histogram.Accumulate(pixelsInRegion, counts, binCount, exclude, cancellationToken);
                    continue;
                }

                for (int y = region.Y; y < region.Bottom; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    pixels += Histogram.Accumulate(buffer.AsSpan((y * frame.Width) + region.X, region.Width), counts, binCount, exclude, cancellationToken);
                }
            }

            return new ImageHistogram(counts, pixels, binCount);
        }
    }

    public static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= NegativeOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        return ImageProcessingContext.AnalyzeNegative(image, options, cancellationToken);
    }

    public static FilmFrame? FindFilmFrame(Image image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return ImageProcessingContext.FindFilmFrame(image, cancellationToken);
    }

    public static Rectangle? FindTrimBounds(Image image, AutoCropOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        return TrimBounds.Find(image.Frames, image.Width, image.Height, options ?? AutoCropOptions.Default, cancellationToken);
    }
}
