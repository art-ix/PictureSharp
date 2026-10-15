namespace PictureSharp.Formats.WebP;

/// <summary>Predictive filter applied to the alpha plane of lossy images.</summary>
public enum WebPAlphaFilter
{
    /// <summary>No filtering.</summary>
    None = 0,
    /// <summary>Pick a filter with a fast heuristic.</summary>
    Fast = 1,
    /// <summary>Try all filters and keep the smallest result.</summary>
    Best = 2,
}
