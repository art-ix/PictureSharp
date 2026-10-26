using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>
/// A colour description as ITU-T H.273 code points (CICP), as stored in a PNG cICP chunk or a HEIF nclx colour box, or
/// an equivalent enumerated description such as JPEG XL's. Code 2 means "unspecified" for each code point.
/// </summary>
public sealed record CicpColorDescription
{
    /// <summary>Creates a description from H.273 code points.</summary>
    /// <param name="colorPrimaries">ColourPrimaries, 0–255 (1 = BT.709/sRGB, 9 = BT.2020, 12 = Display P3).</param>
    /// <param name="transferCharacteristics">TransferCharacteristics, 0–255 (13 = sRGB, 16 = PQ, 18 = HLG).</param>
    /// <param name="matrixCoefficients">MatrixCoefficients, 0–255 (0 = identity/RGB, 1 = BT.709, 9 = BT.2020 NCL).</param>
    /// <param name="fullRange">VideoFullRangeFlag: true for full-range samples.</param>
    /// <exception cref="ArgumentOutOfRangeException">A code point is outside 0–255.</exception>
    public CicpColorDescription(int colorPrimaries, int transferCharacteristics, int matrixCoefficients, bool fullRange)
    {
        ColorPrimaries = Check.Range(colorPrimaries, 0, 255);
        TransferCharacteristics = Check.Range(transferCharacteristics, 0, 255);
        MatrixCoefficients = Check.Range(matrixCoefficients, 0, 255);
        FullRange = fullRange;
    }

    /// <summary>sRGB: primaries 1, transfer 13, identity matrix, full range.</summary>
    public static CicpColorDescription Srgb { get; } = new(1, 13, 0, fullRange: true);

    /// <summary>H.273 ColourPrimaries.</summary>
    public int ColorPrimaries { get; }

    /// <summary>H.273 TransferCharacteristics.</summary>
    public int TransferCharacteristics { get; }

    /// <summary>H.273 MatrixCoefficients; 0 for RGB samples.</summary>
    public int MatrixCoefficients { get; }

    /// <summary>H.273 VideoFullRangeFlag.</summary>
    public bool FullRange { get; }

    /// <summary>Whether the transfer is an HDR curve: PQ (SMPTE ST 2084, 16) or HLG (ARIB STD-B67, 18).</summary>
    public bool IsHdrTransfer => TransferCharacteristics is 16 or 18;
}
