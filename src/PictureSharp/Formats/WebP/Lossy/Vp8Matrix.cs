namespace PictureSharp.Formats.WebP.Lossy;

internal sealed class Vp8Matrix
{
    public ushort[] Q { get; } = new ushort[16];
    public ushort[] Iq { get; } = new ushort[16];
    public uint[] Bias { get; } = new uint[16];
    public uint[] ZeroThreshold { get; } = new uint[16];
    public ushort[] Sharpen { get; } = new ushort[16];

    // port: src/enc/quant_enc.c kBiasMatrices, kFreqSharpening
    private static ReadOnlySpan<byte> BiasMatrices => [96, 110, 96, 108, 110, 115];
    private static ReadOnlySpan<byte> FrequencySharpening => [0, 30, 60, 90, 30, 60, 90, 90, 60, 90, 90, 90, 90, 90, 90, 90];

    // port: src/enc/quant_enc.c ExpandMatrix
    public int Expand(int dc, int ac, int type)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++)
        {
            Q[i] = (ushort)(i == 0 ? dc : ac);
            Iq[i] = (ushort)((1 << 17) / Q[i]);
            Bias[i] = (uint)BiasMatrices[2 * type + (i == 0 ? 0 : 1)] << 9;
            ZeroThreshold[i] = ((1 << 17) - 1 - Bias[i]) / Iq[i];
            Sharpen[i] = type == 0 ? (ushort)((FrequencySharpening[i] * Q[i]) >> 11) : (ushort)0;
            sum += Q[i];
        }
        return (sum + 8) >> 4;
    }
}
