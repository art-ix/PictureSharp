namespace PictureSharp;

/// <summary>Whether a stored image is coded with or without loss (see <see cref="ImageInfo.Compression"/>).</summary>
public enum ImageCompression
{
    /// <summary>The header does not tell (HEVC can be lossless; JPEG XL without XYB can be either).</summary>
    Unknown = 0,

    /// <summary>Every frame is coded without loss.</summary>
    Lossless = 1,

    /// <summary>Every frame is coded with loss.</summary>
    Lossy = 2,

    /// <summary>Frames differ: an animation with lossy and lossless frames (WebP).</summary>
    Mixed = 3,
}
