namespace PictureSharp.Processing;

/// <summary>Edges of a <see cref="FilmFrame"/>, in the straightened image.</summary>
[Flags]
public enum FilmFrameEdges
{
    /// <summary>No edge.</summary>
    None = 0,

    /// <summary>The left edge.</summary>
    Left = 1,

    /// <summary>The top edge.</summary>
    Top = 2,

    /// <summary>The right edge.</summary>
    Right = 4,

    /// <summary>The bottom edge.</summary>
    Bottom = 8,
}
