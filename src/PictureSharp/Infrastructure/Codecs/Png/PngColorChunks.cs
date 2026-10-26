using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Png;

/// <summary>
/// The PNG color-space chunks gAMA, cHRM and sRGB. Like libpng, only the first of each before the image data is used,
/// and invalid values are ignored rather than rejected.
/// </summary>
// Spec: PNG 3rd edition §11.3.3 (cHRM, gAMA, sRGB); invalid values are ignored as libpng does.
internal static class PngColorChunks
{
    public static float? ReadGamma(ReadOnlySpan<byte> body)
    {
        if (body.Length != 4) return null;
        uint value = BinaryPrimitives.ReadUInt32BigEndian(body);
        return value is 0 or > int.MaxValue ? null : value / 100000f;
    }

    public static CieChromaticities? ReadChromaticities(ReadOnlySpan<byte> body)
    {
        if (body.Length != 32) return null;
        Span<float> v = stackalloc float[8];
        for (int i = 0; i < v.Length; i++)
        {
            uint raw = BinaryPrimitives.ReadUInt32BigEndian(body[(i * 4)..]);
            if (raw > 100000) return null;
            v[i] = raw / 100000f;
        }

        try
        {
            return new CieChromaticities(v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7]);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static RenderingIntent? ReadSrgb(ReadOnlySpan<byte> body) =>
        body.Length == 1 && body[0] <= 3 ? (RenderingIntent)body[0] : null;

    /// <summary>A cICP body, or null unless it has 4 bytes, the RGB matrix (PNG allows no other) and a 0/1 range flag.</summary>
    public static CicpColorDescription? ReadCicp(ReadOnlySpan<byte> body) =>
        body.Length == 4 && body[2] == 0 && body[3] <= 1 ? new CicpColorDescription(body[0], body[1], 0, body[3] == 1) : null;

    public static byte[] Gamma(float gamma)
    {
        // ImageMetadata.Gamma keeps the value within the gAMA range.
        var body = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(body, (uint)Math.Clamp(Math.Round(gamma * 100000.0), 1, int.MaxValue));
        return body;
    }

    public static byte[] Chromaticities(CieChromaticities c)
    {
        var body = new byte[32];
        ReadOnlySpan<float> values = [c.WhiteX, c.WhiteY, c.RedX, c.RedY, c.GreenX, c.GreenY, c.BlueX, c.BlueY];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(i * 4), (uint)Math.Round(values[i] * 100000.0));
        }

        return body;
    }
}
