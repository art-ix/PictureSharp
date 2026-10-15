namespace PictureSharp.Processing;

/// <summary>
/// How <see cref="ImageProcessingContext.DrawImage(Image, System.Drawing.Point, DrawImageOptions)"/> mixes the source
/// color with the backdrop color before source-over compositing: the separable blend modes of W3C Compositing and
/// Blending Level 1, as CSS <c>mix-blend-mode</c>. Cb is the backdrop, Cs the source, both 0–1.
/// </summary>
public enum BlendMode
{
    /// <summary>The source color (plain source-over, as Pillow's <c>Image.alpha_composite</c>).</summary>
    Normal = 0,
    /// <summary>Cb · Cs: darkens; white is neutral.</summary>
    Multiply,
    /// <summary>Cb + Cs − Cb · Cs: lightens; black is neutral.</summary>
    Screen,
    /// <summary>Multiply or Screen depending on the backdrop: raises contrast, keeps the backdrop's highlights and shadows.</summary>
    Overlay,
    /// <summary>min(Cb, Cs).</summary>
    Darken,
    /// <summary>max(Cb, Cs).</summary>
    Lighten,
    /// <summary>|Cb − Cs|.</summary>
    Difference,
    /// <summary>Multiply or Screen depending on the source: Overlay with the layers swapped.</summary>
    HardLight,
    /// <summary>A softer Hard Light: darkens or lightens the backdrop depending on the source.</summary>
    SoftLight,
    /// <summary>Brightens the backdrop to reflect the source: Cb / (1 − Cs).</summary>
    ColorDodge,
    /// <summary>Darkens the backdrop to reflect the source: 1 − (1 − Cb) / Cs.</summary>
    ColorBurn,
    /// <summary>Cb + Cs − 2 · Cb · Cs: like Difference with lower contrast.</summary>
    Exclusion,
}
