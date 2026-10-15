using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Maps <see cref="AnchorPosition"/> to Pillow-style centering factors.</summary>
internal static class Anchor
{
    public static (double X, double Y) Centering(AnchorPosition position) => position switch
    {
        AnchorPosition.Center => (0.5, 0.5),
        AnchorPosition.Top => (0.5, 0),
        AnchorPosition.Bottom => (0.5, 1),
        AnchorPosition.Left => (0, 0.5),
        AnchorPosition.Right => (1, 0.5),
        AnchorPosition.TopLeft => (0, 0),
        AnchorPosition.TopRight => (1, 0),
        AnchorPosition.BottomLeft => (0, 1),
        AnchorPosition.BottomRight => (1, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(position), position, "Unknown anchor position."),
    };

    /// <summary>Offset of an <paramref name="inner"/>-long span inside an <paramref name="outer"/>-long one; may be negative.</summary>
    // port: Pillow src/PIL/ImageOps.py pad (Python round() rounds half to even, as Math.Round does)
    public static int Offset(int outer, int inner, double centering) => (int)Math.Round((outer - inner) * centering);

    /// <summary>Top-left corner of an <paramref name="inner"/> area placed in an <paramref name="outer"/> area.</summary>
    public static System.Drawing.Point Place(System.Drawing.Size outer, System.Drawing.Size inner, AnchorPosition position)
    {
        (double cx, double cy) = Centering(position);
        return new(Offset(outer.Width, inner.Width, cx), Offset(outer.Height, inner.Height, cy));
    }
}
