namespace PictureSharp.Processing;

/// <summary>A channel counted by <see cref="ImageHistogram"/>.</summary>
public enum HistogramChannel
{
    /// <summary>The red channel.</summary>
    Red = 0,
    /// <summary>The green channel.</summary>
    Green,
    /// <summary>The blue channel.</summary>
    Blue,
    /// <summary>The alpha channel.</summary>
    Alpha,
    /// <summary>BT.601 luma of the color, as <see cref="ImageProcessingContext.Grayscale"/> and Pillow's "L" conversion; alpha is ignored.</summary>
    Luminance,
}
