namespace PictureSharp;

/// <summary>How colour samples relate to alpha (see <see cref="ImageColorInfo.AlphaMode"/>).</summary>
public enum ImageAlphaMode
{
    /// <summary>No alpha channel and no transparency.</summary>
    None = 0,

    /// <summary>
    /// Straight (unassociated) alpha; also binary transparency (PNG tRNS, GIF transparent index, the ICO AND mask).
    /// </summary>
    Straight = 1,

    /// <summary>Premultiplied (associated) alpha (TIFF ExtraSamples 1, HEIF prem, JPEG XL alpha_associated).</summary>
    Premultiplied = 2,
}
