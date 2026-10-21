namespace PictureSharp.Formats.Jpeg;

/// <summary>Chroma sampling for color JPEG images. Images written with one component (see <see cref="JpegColorMode"/>) have no chroma.</summary>
public enum JpegSubsampling
{
    /// <summary>Full chroma resolution.</summary>
    Ratio444,
    /// <summary>Half horizontal chroma resolution.</summary>
    Ratio422,
    /// <summary>Half horizontal and vertical chroma resolution (the default).</summary>
    Ratio420,
    /// <summary>Half vertical chroma resolution.</summary>
    Ratio440,
}
