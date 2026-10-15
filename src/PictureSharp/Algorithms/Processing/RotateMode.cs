namespace PictureSharp.Processing;

/// <summary>Quarter-turn rotations, clockwise.</summary>
public enum RotateMode
{
    /// <summary>No rotation.</summary>
    None = 0,
    /// <summary>90° clockwise.</summary>
    Rotate90 = 90,
    /// <summary>180°.</summary>
    Rotate180 = 180,
    /// <summary>270° clockwise (90° counter-clockwise).</summary>
    Rotate270 = 270,
}
