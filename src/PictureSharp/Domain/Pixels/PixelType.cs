namespace PictureSharp;

/// <summary>The pixel storage of an <see cref="Image"/>, shared by all its frames.</summary>
public enum PixelType
{
    /// <summary><see cref="PictureSharp.Rgba32"/>: 8 bits per channel, the default.</summary>
    Rgba32,
    /// <summary><see cref="PictureSharp.Rgba64"/>: 16 bits per channel.</summary>
    Rgba64,
}
