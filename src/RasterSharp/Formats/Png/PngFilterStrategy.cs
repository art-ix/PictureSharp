namespace RasterSharp;

/// <summary>PNG scanline filter selection.</summary>
public enum PngFilterStrategy
{
    /// <summary>Choose per row with the minimum-sum-of-absolute-differences heuristic.</summary>
    Adaptive = 0,
    /// <summary>Filter type 0.</summary>
    None,
    /// <summary>Filter type 1.</summary>
    Sub,
    /// <summary>Filter type 2.</summary>
    Up,
    /// <summary>Filter type 3.</summary>
    Average,
    /// <summary>Filter type 4.</summary>
    Paeth,
}
