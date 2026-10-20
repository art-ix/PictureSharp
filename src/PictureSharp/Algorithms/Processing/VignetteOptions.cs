namespace PictureSharp.Processing;

/// <summary>
/// A radial vignette for <see cref="ImageProcessingContext.Vignette(VignetteOptions)"/>. Distances are measured from the
/// image center relative to half its diagonal, so 1 is a corner; the vignette starts at <see cref="Midpoint"/> and
/// reaches full strength at <c>Midpoint + Feather · (1 − Midpoint)</c>, with a smoothstep between.
/// </summary>
public sealed record VignetteOptions
{
    /// <summary>
    /// Strength from −1 to 1: positive values darken the edges (by up to 2 stops at 1), negative values brighten them;
    /// 0 leaves the image unchanged. Default: 0.
    /// </summary>
    public float Amount
    {
        get;
        init => field = value is >= -1f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(Amount), value, "Amount must be between -1 and 1.");
    }

    /// <summary>Distance (0–1) where the vignette starts; the area inside it is unchanged. Default: 0.5.</summary>
    public float Midpoint
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(Midpoint), value, "Midpoint must be between 0 and 1.");
    } = 0.5f;

    /// <summary>
    /// Width (0–1) of the transition, as a fraction of the distance from <see cref="Midpoint"/> to the corner; 0 gives a
    /// hard edge, 1 (the default) a transition that ends in the corners.
    /// </summary>
    public float Feather
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(Feather), value, "Feather must be between 0 and 1.");
    } = 1f;
}
