namespace RasterSharp.Formats.Jpeg;

/// <summary>JPEG marker codes (the byte following 0xFF).</summary>
internal static class JpegMarkers
{
    public const int Sof0 = 0xC0;     // baseline DCT
    public const int Sof1 = 0xC1;     // extended sequential DCT, Huffman
    public const int Sof2 = 0xC2;     // progressive DCT, Huffman
    public const int Sof3 = 0xC3;     // lossless, Huffman
    public const int Dht = 0xC4;
    public const int Sof15 = 0xCF;
    public const int Dac = 0xCC;
    public const int Rst0 = 0xD0;
    public const int Rst7 = 0xD7;
    public const int Soi = 0xD8;
    public const int Eoi = 0xD9;
    public const int Sos = 0xDA;
    public const int Dqt = 0xDB;
    public const int Dnl = 0xDC;
    public const int Dri = 0xDD;
    public const int App0 = 0xE0;
    public const int App1 = 0xE1;
    public const int App2 = 0xE2;
    public const int App14 = 0xEE;
    public const int Tem = 0x01;

    /// <summary>
    /// Skips to the next marker at or after <paramref name="pos"/>, ignoring garbage bytes, fill bytes and stuffed
    /// zeros. Returns the marker code with <paramref name="pos"/> at its first 0xFF and <paramref name="after"/>
    /// just past it; at the end of the data returns <see cref="Eoi"/>, as libjpeg's data source does.
    /// </summary>
    // port: libjpeg-turbo jdmarker.c next_marker
    public static int Next(ReadOnlySpan<byte> data, ref int pos, out int after)
    {
        while (true)
        {
            int ff = data[Math.Min(pos, data.Length)..].IndexOf((byte)0xFF);
            if (ff < 0)
            {
                pos = after = data.Length;
                return Eoi;
            }

            pos += ff;
            int p = pos + 1;
            while (p < data.Length && data[p] == 0xFF)
            {
                p++;
            }

            if (p >= data.Length)
            {
                pos = after = data.Length;
                return Eoi;
            }

            if (data[p] != 0)
            {
                after = p + 1;
                return data[p];
            }

            pos = p + 1;
        }
    }
}
