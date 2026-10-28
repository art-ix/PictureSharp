using System.Buffers.Binary;
using PictureSharp.Formats.Png;

namespace PictureSharp.Infrastructure.Codecs.Png;

/// <summary>
/// The gAMA, cHRM, sRGB and sBIT chunks of one PNG. Callers pass only chunks before the first IDAT; like libpng, the first
/// of each type wins even if invalid, and invalid values are ignored rather than rejected.
/// </summary>
internal sealed class PngColorDescription
{
    public const uint Gama = 0x67414d41, Chrm = 0x6348524d, Srgb = 0x73524742, Sbit = 0x73424954;

    private bool _seenGamma, _seenChromaticities, _seenSrgb;
    private bool _seenSignificantBits;
    private int _bitDepth;
    private PngColor _color;

    public PngSignificantBits? SignificantBits { get; private set; }

    public void SetHeader(int bitDepth, PngColor color) => (_bitDepth, _color) = (bitDepth, color);

    public static PngColorType ColorType(PngColor color) => color switch
    {
        PngColor.Gray => PngColorType.Grayscale,
        PngColor.GrayAlpha => PngColorType.GrayscaleAlpha,
        PngColor.Rgb => PngColorType.Rgb,
        PngColor.Rgba => PngColorType.Rgba,
        _ => PngColorType.Palette,
    };

    public float? Gamma { get; private set; }

    public CieChromaticities? Chromaticities { get; private set; }

    public RenderingIntent? SrgbIntent { get; private set; }

    /// <summary>The chunk type as a big-endian code.</summary>
    public static uint TypeOf(ReadOnlySpan<byte> type) => BinaryPrimitives.ReadUInt32BigEndian(type);

    /// <summary>Whether <paramref name="type"/> is a color chunk not seen yet.</summary>
    public bool Wants(uint type) => type switch
    {
        Gama => !_seenGamma,
        Chrm => !_seenChromaticities,
        Srgb => !_seenSrgb,
        Sbit => !_seenSignificantBits,
        _ => false,
    };

    /// <summary>Records a chunk accepted by <see cref="Wants"/>.</summary>
    public void Read(uint type, ReadOnlySpan<byte> body)
    {
        switch (type)
        {
            case Gama: _seenGamma = true; Gamma = PngColorChunks.ReadGamma(body); break;
            case Chrm: _seenChromaticities = true; Chromaticities = PngColorChunks.ReadChromaticities(body); break;
            case Srgb: _seenSrgb = true; SrgbIntent = PngColorChunks.ReadSrgb(body); break;
            case Sbit:
                _seenSignificantBits = true;
                try { SignificantBits = new PngSignificantBits(_bitDepth, ColorType(_color), body); }
                catch (ArgumentException) { } // Invalid ancillary precision does not change pixel decoding.
                break;
        }
    }

    public void ApplyTo(ImageMetadata metadata)
    {
        metadata.Gamma = Gamma;
        metadata.Chromaticities = Chromaticities;
        metadata.SrgbRenderingIntent = SrgbIntent;
        if (SignificantBits is { } bits) metadata.SetFormatMetadata(bits);
    }
}
