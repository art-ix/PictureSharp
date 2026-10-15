using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Png;

/// <summary>
/// The gAMA, cHRM and sRGB chunks of one PNG. Callers pass only chunks before the first IDAT; like libpng, the first
/// of each type wins even if invalid, and invalid values are ignored rather than rejected.
/// </summary>
internal sealed class PngColorDescription
{
    public const uint Gama = 0x67414d41, Chrm = 0x6348524d, Srgb = 0x73524742;

    private bool _seenGamma, _seenChromaticities, _seenSrgb;

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
        }
    }

    public void ApplyTo(ImageMetadata metadata)
    {
        metadata.Gamma = Gamma;
        metadata.Chromaticities = Chromaticities;
        metadata.SrgbRenderingIntent = SrgbIntent;
    }
}
