using System.Drawing;
using RasterSharp.Internal;

namespace RasterSharp;

/// <summary>
/// Full control over <see cref="ImageProcessingContext.Resize(ResizeOptions)"/>. The geometry follows Pillow:
/// <see cref="ResizeMode.Max"/>, <see cref="ResizeMode.Crop"/> and <see cref="ResizeMode.Pad"/> match
/// <c>ImageOps.contain</c>, <c>ImageOps.fit</c> and <c>ImageOps.pad</c>, with <see cref="Position"/> as <c>centering</c>.
/// </summary>
public sealed record ResizeOptions
{
    /// <summary>Requested size. One side may be 0 to derive it from the aspect ratio.</summary>
    public required Size Size
    {
        get;
        init => field = value.Width >= 0 && value.Height >= 0 && (value.Width | value.Height) != 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(Size), value, "Width and height must not be negative, and at least one must be positive.");
    }

    /// <summary>How the image is mapped onto <see cref="Size"/>. Default: <see cref="ResizeMode.Stretch"/>.</summary>
    public ResizeMode Mode { get; init => field = Check.Defined(value); }

    /// <summary>Interpolation kernel. Default: <see cref="ResamplingFilter.Lanczos3"/>.</summary>
    public ResamplingFilter Filter { get; init => field = Check.Defined(value); } = ResamplingFilter.Lanczos3;

    /// <summary>Which part is kept by <see cref="ResizeMode.Crop"/> and where <see cref="ResizeMode.Pad"/> places the image. Default: center.</summary>
    public AnchorPosition Position { get; init => field = Check.Defined(value); }

    /// <summary>Border color for <see cref="ResizeMode.Pad"/>. Default: transparent black.</summary>
    public Rgba32 PadColor { get; init; }

    /// <summary>
    /// Whether the image may be enlarged. When false the scale is capped at 1: images that would be enlarged keep
    /// their size (<see cref="ResizeMode.Crop"/> only crops, <see cref="ResizeMode.Pad"/> only pads). Default: true.
    /// </summary>
    public bool AllowUpscale { get; init; } = true;
}
