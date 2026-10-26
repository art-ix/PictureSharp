namespace PictureSharp;

/// <summary>A resolution in dots per inch, as declared by a file (see <see cref="ImageInfo.Resolution"/>).</summary>
public readonly record struct ImageResolution
{
    /// <summary>Creates a resolution.</summary>
    /// <param name="horizontal">Horizontal resolution in DPI; positive and finite.</param>
    /// <param name="vertical">Vertical resolution in DPI; positive and finite.</param>
    /// <exception cref="ArgumentOutOfRangeException">A value is not positive and finite.</exception>
    public ImageResolution(float horizontal, float vertical)
    {
        Horizontal = Positive(horizontal, nameof(horizontal));
        Vertical = Positive(vertical, nameof(vertical));
    }

    /// <summary>Horizontal resolution in DPI.</summary>
    public float Horizontal { get; }

    /// <summary>Vertical resolution in DPI.</summary>
    public float Vertical { get; }

    /// <summary>A resolution read from a file, or null unless both values are positive and finite as floats.</summary>
    internal static ImageResolution? FromDeclared(double horizontal, double vertical) =>
        (float)horizontal is > 0 and var x && float.IsFinite(x) && (float)vertical is > 0 and var y && float.IsFinite(y)
            ? new ImageResolution(x, y)
            : null;

    private static float Positive(float value, string name) =>
        value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name, value, "Expected a positive, finite number.");
}
