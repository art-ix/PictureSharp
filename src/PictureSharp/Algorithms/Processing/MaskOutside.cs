namespace PictureSharp.Processing;

/// <summary>What happens to the pixels of the image that the mask does not cover.</summary>
public enum MaskOutside
{
    /// <summary>They are kept unchanged.</summary>
    Keep = 0,
    /// <summary>They become fully transparent (their color is kept).</summary>
    Transparent,
}
