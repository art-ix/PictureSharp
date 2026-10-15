namespace PictureSharp.Processing;

/// <summary>How <see cref="ImageProcessingContext.AutoCrop(AutoCropOptions?)"/> and <c>Image.FindTrimBounds</c> find the content.</summary>
public sealed record AutoCropOptions
{
    /// <summary>Default options: trim fully transparent margins, without padding.</summary>
    public static AutoCropOptions Default { get; } = new();

    /// <summary>What to trim. Default: <see cref="AutoCropMode.Alpha"/>.</summary>
    public AutoCropMode Mode
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Mode), value, "Unknown auto-crop mode.");
    }

    /// <summary>
    /// <see cref="AutoCropMode.Alpha"/>: pixels with alpha at or below this value count as margin, so 0 keeps every
    /// visible pixel and higher values also drop faint halos and shadows. Default: 0.
    /// </summary>
    public byte AlphaThreshold { get; init; }

    /// <summary>
    /// <see cref="AutoCropMode.Background"/>: the margin color; <see langword="null"/> takes the top-left pixel of the
    /// first frame. Default: null.
    /// </summary>
    public Rgba32? Background { get; init; }

    /// <summary>
    /// <see cref="AutoCropMode.Background"/>: the largest difference in any channel (alpha included) for which a pixel
    /// still counts as background; raise it for noisy or compressed photos. Fully transparent pixels always match a
    /// fully transparent background. Default: 0 (exact match).
    /// </summary>
    public byte Tolerance { get; init; }

    /// <summary>Pixels of margin to keep around the content, limited by the canvas. Default: 0.</summary>
    public int Padding
    {
        get;
        init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Padding), value, "Padding must not be negative.");
    }
}
