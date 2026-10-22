using PictureSharp.Application.Processing;
using PictureSharp.Processing;

namespace PictureSharp;

/// <summary>Read-only analyses of an image's pixels.</summary>
public static class ImageAnalysisExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// Counts the red, green, blue, alpha and luminance values of the image; the image is not modified.
        /// By default every pixel of the first frame is counted, as Pillow's <c>Image.histogram()</c>.
        /// </summary>
        /// <example>
        /// <code>byte median = image.GetHistogram().Percentile(HistogramChannel.Luminance, 50);</code>
        /// </example>
        /// <exception cref="ArgumentOutOfRangeException"><see cref="HistogramOptions.Region"/> is empty or not inside the image.</exception>
        public ImageHistogram GetHistogram(HistogramOptions? options = null) =>
            ImageAnalysisService.GetHistogram(image, options, CancellationToken.None);

        /// <summary>Counts the channel values as <see cref="GetHistogram(Image, HistogramOptions?)"/>; <paramref name="cancellationToken"/> stops it between rows.</summary>
        public ImageHistogram GetHistogram(HistogramOptions? options, CancellationToken cancellationToken) =>
            ImageAnalysisService.GetHistogram(image, options, cancellationToken);

        /// <summary>
        /// Finds the bounding box of the content that <see cref="ImageProcessingContext.AutoCrop(AutoCropOptions?)"/> would keep,
        /// including the padding; the image is not modified. Animations use the union of every frame's content.
        /// </summary>
        /// <returns>The box, or <see langword="null"/> when no pixel differs from the margin (fully transparent or uniform).</returns>
        /// <example>
        /// <code>if (image.FindTrimBounds() is { } box) Console.WriteLine($"content at {box}");</code>
        /// </example>
        public System.Drawing.Rectangle? FindTrimBounds(AutoCropOptions? options = null) =>
            ImageAnalysisService.FindTrimBounds(image, options, CancellationToken.None);

        /// <summary>Finds the content box as <see cref="FindTrimBounds(Image, AutoCropOptions?)"/>; <paramref name="cancellationToken"/> stops it between rows.</summary>
        public System.Drawing.Rectangle? FindTrimBounds(AutoCropOptions? options, CancellationToken cancellationToken) =>
            ImageAnalysisService.FindTrimBounds(image, options, cancellationToken);

        /// <summary>
        /// Measures a photographed film negative without changing it: the film base (from
        /// <see cref="NegativeOptions.FilmBaseRegion"/>, or else the lightest pixels), the density of each channel that
        /// prints white and the midtone gammas, on the stored channel values of <see cref="NegativeOptions.Region"/>
        /// (in the image's own color space, decoded with the sRGB curve). Fully transparent pixels and pixels with a
        /// channel at 0–2 or 253–255 are left out of the measurement.
        /// </summary>
        /// <example>
        /// <code>
        /// NegativeAnalysis analysis = image.AnalyzeNegative(new NegativeOptions { Region = frame, FilmBaseRegion = rebate });
        /// if (analysis.Warnings.HasFlag(NegativeWarnings.ChannelClipped)) Console.WriteLine("Use a bluish backlight.");
        /// image.Mutate(x => x.DevelopNegative(new NegativeOptions { Calibration = analysis.Calibration }));
        /// </code>
        /// </example>
        /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A region of <paramref name="options"/> is empty or not inside the image.</exception>
        public NegativeAnalysis AnalyzeNegative(NegativeOptions? options = null) =>
            ImageAnalysisService.AnalyzeNegative(image, options, CancellationToken.None);

        /// <summary>Measures the negative as <see cref="AnalyzeNegative(Image, NegativeOptions?)"/>; <paramref name="cancellationToken"/> stops it between rows.</summary>
        public NegativeAnalysis AnalyzeNegative(NegativeOptions? options, CancellationToken cancellationToken) =>
            ImageAnalysisService.AnalyzeNegative(image, options, cancellationToken);
    }
}
