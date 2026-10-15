namespace PictureSharp.Processing;

/// <summary>The color channels a tonal operation such as <see cref="ImageProcessingContext.Levels(LevelsOptions)"/> changes; alpha is never changed.</summary>
[Flags]
public enum ColorChannels
{
    /// <summary>No channel; not valid for an operation.</summary>
    None = 0,
    /// <summary>The red channel.</summary>
    Red = 1,
    /// <summary>The green channel.</summary>
    Green = 2,
    /// <summary>The blue channel.</summary>
    Blue = 4,
    /// <summary>Red, green and blue.</summary>
    Rgb = Red | Green | Blue,
}
