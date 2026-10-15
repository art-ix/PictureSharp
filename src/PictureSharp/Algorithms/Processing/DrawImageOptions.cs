namespace PictureSharp.Processing;

/// <summary>How <see cref="ImageProcessingContext.DrawImage(Image, System.Drawing.Point, DrawImageOptions)"/> blends the source.</summary>
public sealed record DrawImageOptions
{
    /// <summary>Default options: full opacity, blending on encoded values like Pillow.</summary>
    public static DrawImageOptions Default { get; } = new();

    /// <summary>0–1, multiplies the source alpha. Default: 1.</summary>
    public float Opacity
    {
        get;
        init => field = value is >= 0f and <= 1f ? value : throw new ArgumentOutOfRangeException(nameof(Opacity), value, "Opacity must be between 0 and 1.");
    } = 1f;

    /// <summary>
    /// Whether to blend in linear-light sRGB with float precision instead of on the encoded values like Pillow's
    /// <c>Image.alpha_composite</c>. Linear blending avoids dark fringes between saturated colors and at
    /// semi-transparent edges; it assumes sRGB pixels in both images. Default: false.
    /// </summary>
    public bool LinearLight { get; init; }
}
