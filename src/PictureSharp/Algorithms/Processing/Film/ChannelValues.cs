namespace PictureSharp.Processing;

/// <summary>One number for each color channel, such as a film-base color or a density range.</summary>
/// <param name="Red">The value of the red channel.</param>
/// <param name="Green">The value of the green channel.</param>
/// <param name="Blue">The value of the blue channel.</param>
public readonly record struct ChannelValues(float Red, float Green, float Blue)
{
    /// <summary>The value of channel 0 (red), 1 (green) or 2 (blue).</summary>
    internal float this[int channel] => channel switch { 0 => Red, 1 => Green, _ => Blue };
}
