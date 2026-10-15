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

    /// <summary>
    /// How the source color mixes with the backdrop before compositing (see <see cref="Processing.BlendMode"/>).
    /// <see cref="BlendMode.Normal"/> keeps the Pillow-compatible blend; the other modes compute in float precision
    /// following W3C Compositing and Blending Level 1, in linear light when <see cref="LinearLight"/> is set.
    /// Default: <see cref="BlendMode.Normal"/>.
    /// </summary>
    public BlendMode BlendMode
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(BlendMode), value, "Unknown blend mode.");
    }
}
