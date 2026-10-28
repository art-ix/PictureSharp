using System.Runtime.CompilerServices;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.ColorManagement;

internal static partial class SrgbTransfer
{
    /// <summary>Linear light of a 16-bit sRGB value; an exact widening <c>v · 257</c> gives <see cref="Decode(byte)"/> of v.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Decode16(ushort value) => Tables16.Decode[value];

    /// <summary>Linear light of every 16-bit sRGB value (shared; never written).</summary>
    public static float[] DecodeTable16 => Tables16.Decode;

    /// <summary>
    /// The encoded value of linear light <paramref name="linear"/> clamped to [0, 1], unquantized; within 2e-6 of
    /// <see cref="EncodeExact"/> and identical on every architecture.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float EncodeUnit(float linear)
    {
        if (!(linear > 0f))
        {
            return 0f;
        }

        if (linear >= 1f)
        {
            return 1f;
        }

        return linear <= 0.0031308f ? linear * 12.92f : (1.055f * DeterministicMath.Pow(linear, 1f / 2.4f)) - 0.055f;
    }

    // One entry per 16-bit value (256 KB), built on first use like the 8-bit tables.
    private static class Tables16
    {
        public static readonly float[] Decode = BuildDecode();

        private static float[] BuildDecode()
        {
            var table = new float[ushort.MaxValue + 1];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = (float)ToneCurve.Srgb.Evaluate(i / 65535.0);
            }

            return table;
        }
    }
}
