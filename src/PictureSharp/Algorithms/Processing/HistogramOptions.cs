using System.Drawing;

namespace PictureSharp.Processing;

/// <summary>Which pixels a histogram counts.</summary>
public sealed record HistogramOptions
{
    /// <summary>Default options: every pixel of the first frame.</summary>
    public static HistogramOptions Default { get; } = new();

    /// <summary>Whether fully transparent pixels are counted. Default: <see cref="HistogramTransparency.Include"/>.</summary>
    public HistogramTransparency Transparency
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Transparency), value, "Unknown transparency policy.");
    }

    /// <summary>The area to count, which must lie inside the image and not be empty; <see langword="null"/> counts the whole canvas. Default: null.</summary>
    public Rectangle? Region { get; init; }

    /// <summary>Whether to count every frame of an animation instead of only the first. Default: false.</summary>
    public bool AllFrames { get; init; }

    /// <summary>
    /// Whether to count one bin per possible sample value (<see cref="ImageHistogram.BinCount"/> 256 on
    /// <see cref="PixelType.Rgba32"/> images, 65536 on <see cref="PixelType.Rgba64"/> ones) instead of 256 bins.
    /// Default: false (256 bins, the nearest to each sample on 16-bit images).
    /// </summary>
    public bool FullResolution { get; init; }
}
