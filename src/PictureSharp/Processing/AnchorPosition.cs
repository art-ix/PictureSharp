namespace PictureSharp;

/// <summary>Where an image is anchored when it is cropped, padded or placed on a larger area.</summary>
public enum AnchorPosition
{
    /// <summary>Centered on both axes.</summary>
    Center = 0,
    /// <summary>Top edge, centered horizontally.</summary>
    Top,
    /// <summary>Bottom edge, centered horizontally.</summary>
    Bottom,
    /// <summary>Left edge, centered vertically.</summary>
    Left,
    /// <summary>Right edge, centered vertically.</summary>
    Right,
    /// <summary>Top-left corner.</summary>
    TopLeft,
    /// <summary>Top-right corner.</summary>
    TopRight,
    /// <summary>Bottom-left corner.</summary>
    BottomLeft,
    /// <summary>Bottom-right corner.</summary>
    BottomRight,
}
