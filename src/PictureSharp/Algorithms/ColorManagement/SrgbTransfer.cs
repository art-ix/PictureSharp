using System.Runtime.CompilerServices;

namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>The sRGB transfer function (IEC 61966-2-1) between 8-bit encoded values and linear light.</summary>
internal static partial class SrgbTransfer
{
    // Linear light is quantized to 16 bits before encoding; one step changes the 8-bit result by at most 0.05.
    private const int EncodeSteps = 65535;

    /// <summary>Linear light of each 8-bit sRGB value.</summary>
    public static ReadOnlySpan<float> DecodeTable => Tables.Decode;

    /// <summary>The 8-bit sRGB values of the 65536 linear steps that <see cref="EncodeIndex(float)"/> selects.</summary>
    public static ReadOnlySpan<byte> EncodeTable => Tables.Encode;

    /// <summary>Linear light of an 8-bit sRGB value.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Decode(byte value) => Tables.Decode[value];

    /// <summary>The nearest 8-bit sRGB value of linear light <paramref name="linear"/>, clamped to [0, 1].</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Encode(float linear) => Tables.Encode[EncodeIndex(linear)];

    /// <summary>The index in <see cref="EncodeTable"/> of linear light <paramref name="linear"/>.</summary>
    // NaN compares false and maps to 0.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int EncodeIndex(float linear) => linear > 0f ? (linear < 1f ? (int)((linear * EncodeSteps) + 0.5f) : EncodeSteps) : 0;

    /// <summary>The exact encoded value of linear light in [0, 1].</summary>
    public static double EncodeExact(double linear)
    {
        double v = Math.Clamp(linear, 0, 1);
        return v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
    }

    // Built on first use, so operations that do not convert colors do not pay for the tables.
    private static class Tables
    {
        public static readonly float[] Decode = BuildDecode();
        public static readonly byte[] Encode = BuildEncode();

        private static float[] BuildDecode()
        {
            var table = new float[256];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (float)ToneCurve.Srgb.Evaluate(i / 255.0);
            }

            return table;
        }

        private static byte[] BuildEncode()
        {
            var table = new byte[EncodeSteps + 1];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (byte)Math.Round(EncodeExact((double)i / EncodeSteps) * 255, MidpointRounding.AwayFromZero);
            }

            return table;
        }
    }
}
