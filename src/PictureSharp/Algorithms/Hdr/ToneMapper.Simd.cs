using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

internal sealed partial class ToneMapper
{
    /// <summary>
    /// Maps whole groups of four pixels with the operations of <see cref="MapScalar"/> in the same order, so the
    /// bytes are identical; returns the pixels written and whether any of them is not opaque.
    /// </summary>
    internal int MapSimd(ReadOnlySpan<Rgba128> source, Span<Rgba32> target, out bool alpha)
    {
        alpha = false;
        if (!Simd.IsSupported128)
        {
            return 0;
        }

        ReadOnlySpan<byte> encode = SrgbTransfer.EncodeTable;
        ref float s = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba128, float>(source));
        Vector128<float> exposure = Vector128.Create(_exposure);
        Vector128<float> inverseWhiteSquared = Vector128.Create(_inverseWhiteSquared);
        Vector128<int> notOpaque = Vector128<int>.Zero;
        int count = source.Length & ~3;
        for (int i = 0; i < count; i += 4)
        {
            Vector128<int> r = Vector128.LoadUnsafe(ref s, (nuint)(i * 4)).AsInt32();
            Vector128<int> g = Vector128.LoadUnsafe(ref s, (nuint)((i * 4) + 4)).AsInt32();
            Vector128<int> b = Vector128.LoadUnsafe(ref s, (nuint)((i * 4) + 8)).AsInt32();
            Vector128<int> a = Vector128.LoadUnsafe(ref s, (nuint)((i * 4) + 12)).AsInt32();
            Simd.Transpose(ref r, ref g, ref b, ref a);
            (Vector128<float> red, Vector128<float> green, Vector128<float> blue) = (r.AsSingle(), g.AsSingle(), b.AsSingle());
            if (_toSrgb is { } m)
            {
                (red, green, blue) = (
                    (Vector128.Create(m[0]) * red) + (Vector128.Create(m[1]) * green) + (Vector128.Create(m[2]) * blue),
                    (Vector128.Create(m[3]) * red) + (Vector128.Create(m[4]) * green) + (Vector128.Create(m[5]) * blue),
                    (Vector128.Create(m[6]) * red) + (Vector128.Create(m[7]) * green) + (Vector128.Create(m[8]) * blue));
            }

            red *= exposure;
            green *= exposure;
            blue *= exposure;
            Vector128<float> l = (Vector128.Create(0.2126f) * red) + (Vector128.Create(0.7152f) * green) + (Vector128.Create(0.0722f) * blue);
            Vector128<float> scale = (Vector128<float>.One + (l * inverseWhiteSquared)) / (Vector128<float>.One + l);
            Vector128<float> positive = Vector128.GreaterThan(l, Vector128<float>.Zero);
            Vector128<int> ri = SrgbTransfer.EncodeIndex(positive & (red * scale));
            Vector128<int> gi = SrgbTransfer.EncodeIndex(positive & (green * scale));
            Vector128<int> bi = SrgbTransfer.EncodeIndex(positive & (blue * scale));
            Vector128<int> ai = Quantize(a.AsSingle());
            notOpaque |= ~Vector128.Equals(ai, Vector128.Create(255));
            // Constant lane indices: a variable index makes the JIT spill the vectors (slow on ARM64).
            target[i] = new Rgba32(encode[ri[0]], encode[gi[0]], encode[bi[0]], (byte)ai[0]);
            target[i + 1] = new Rgba32(encode[ri[1]], encode[gi[1]], encode[bi[1]], (byte)ai[1]);
            target[i + 2] = new Rgba32(encode[ri[2]], encode[gi[2]], encode[bi[2]], (byte)ai[2]);
            target[i + 3] = new Rgba32(encode[ri[3]], encode[gi[3]], encode[bi[3]], (byte)ai[3]);
        }

        alpha = notOpaque != Vector128<int>.Zero;
        return count;
    }

    // Math.Clamp keeps NaN, which the saturating conversion maps to 0, as the scalar cast does.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Quantize(Vector128<float> v) =>
        Vector128.ConvertToInt32((Vector128.Clamp(v, Vector128<float>.Zero, Vector128<float>.One) * Vector128.Create(255f)) + Vector128.Create(0.5f));
}
