namespace PictureSharp.Domain.Hdr;

/// <summary>
/// The transfer function of a gain map's base image that is not sRGB: linear light of every 8-bit code of R, G and B,
/// and of every 16-bit code for 16-bit bases. Immutable, so attachments and their clones share it; the curves behind
/// it live in the color-management layer.
/// </summary>
internal abstract class BaseTransfer
{
    /// <summary>Linear values of the 256 codes of R, G and B (768 entries).</summary>
    public abstract float[] Codes8 { get; }

    /// <summary>Linear values of the 65536 codes of R, G and B (3 × 65536 entries).</summary>
    public abstract float[] Codes16 { get; }
}
