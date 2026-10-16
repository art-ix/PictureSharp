using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Algorithms.Processing;

// 256-bit forms of Compositor.Blend.Simd.cs for AVX2: the same lane-wise operations on eight pixels.
internal static partial class Compositor
{
    /// <summary>Blends whole groups of eight pixels of a row with AVX2; returns the pixels processed.</summary>
    private static int BlendRowAvx2<TChannels, TMode>(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        ref uint d = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(dstRow));
        ref uint s = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(srcRow));
        Vector256<float> unit = Vector256.Create(255f);
        Vector256<float> one = Vector256<float>.One;
        Vector256<uint> bytes = Vector256.Create(0xFFu);
        int count = dstRow.Length & ~7;
        for (int i = 0; i < count; i += 8)
        {
            Vector256<uint> sp = Vector256.LoadUnsafe(ref s, (nuint)i);
            Vector256<uint> sa = sp >> 24;
            if (sa == Vector256<uint>.Zero)
            {
                continue;
            }

            Vector256<uint> dp = Vector256.LoadUnsafe(ref d, (nuint)i);
            Vector256<float> ab = Vector256.ConvertToSingle((dp >> 24).AsInt32()) / unit;
            Vector256<float> alphaS = (Vector256.ConvertToSingle(sa.AsInt32()) / unit) * Vector256.Create(opacity);
            Vector256<float> backdropWeight = ab * (one - alphaS);
            Vector256<float> ao = alphaS + backdropWeight;
            Vector256<int> r = Channel<TChannels, TMode>(dp & bytes, sp & bytes, ab, alphaS, backdropWeight, ao);
            Vector256<int> g = Channel<TChannels, TMode>((dp >> 8) & bytes, (sp >> 8) & bytes, ab, alphaS, backdropWeight, ao);
            Vector256<int> b = Channel<TChannels, TMode>((dp >> 16) & bytes, (sp >> 16) & bytes, ab, alphaS, backdropWeight, ao);
            Vector256<int> a = Vector256.ConvertToInt32((ao * unit) + Vector256.Create(0.5f));
            Vector256<uint> blended = (r | (g << 8) | (b << 16) | (a << 24)).AsUInt32();
            Vector256.ConditionalSelect(Vector256.Equals(sa, Vector256<uint>.Zero), dp, blended).StoreUnsafe(ref d, (nuint)i);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Channel<TChannels, TMode>(Vector256<uint> backdrop, Vector256<uint> source, Vector256<float> ab,
        Vector256<float> alphaS, Vector256<float> backdropWeight, Vector256<float> ao)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        Vector256<float> cb = TChannels.Decode(backdrop.AsInt32());
        Vector256<float> cs = TChannels.Decode(source.AsInt32());
        Vector256<float> mixed = ((Vector256<float>.One - ab) * cs) + (ab * TMode.Blend(cb, cs));
        return TChannels.Encode(((alphaS * mixed) + (backdropWeight * cb)) / ao);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Screen(Vector256<float> cb, Vector256<float> cs) => cb + cs - (cb * cs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> HardLight(Vector256<float> cb, Vector256<float> cs) =>
        Vector256.ConditionalSelect(Vector256.LessThanOrEqual(cs, Vector256.Create(0.5f)),
            cb * Vector256.Create(2f) * cs, Screen(cb, (Vector256.Create(2f) * cs) - Vector256<float>.One));

    private readonly partial struct NormalMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => cs;
    }

    private readonly partial struct MultiplyMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => cb * cs;
    }

    private readonly partial struct ScreenMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => Screen(cb, cs);
    }

    private readonly partial struct OverlayMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => HardLight(cs, cb);
    }

    private readonly partial struct DarkenMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => Vector256.Min(cb, cs);
    }

    private readonly partial struct LightenMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => Vector256.Max(cb, cs);
    }

    private readonly partial struct DifferenceMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => Vector256.Abs(cb - cs);
    }

    private readonly partial struct HardLightMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => HardLight(cb, cs);
    }

    private readonly partial struct SoftLightMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs)
        {
            Vector256<float> one = Vector256<float>.One, two = Vector256.Create(2f);
            Vector256<float> dark = cb - ((one - (two * cs)) * cb * (one - cb));
            Vector256<float> polynomial = ((((Vector256.Create(16f) * cb) - Vector256.Create(12f)) * cb) + Vector256.Create(4f)) * cb;
            Vector256<float> d = Vector256.ConditionalSelect(Vector256.LessThanOrEqual(cb, Vector256.Create(0.25f)), polynomial, Vector256.Sqrt(cb));
            Vector256<float> light = cb + (((two * cs) - one) * (d - cb));
            return Vector256.ConditionalSelect(Vector256.LessThanOrEqual(cs, Vector256.Create(0.5f)), dark, light);
        }
    }

    private readonly partial struct ColorDodgeMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs)
        {
            Vector256<float> one = Vector256<float>.One;
            Vector256<float> v = Vector256.Min(one, cb / (one - cs));
            v = Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(cs, one), one, v);
            return Vector256.ConditionalSelect(Vector256.Equals(cb, Vector256<float>.Zero), Vector256<float>.Zero, v);
        }
    }

    private readonly partial struct ColorBurnMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs)
        {
            Vector256<float> one = Vector256<float>.One;
            Vector256<float> v = one - Vector256.Min(one, (one - cb) / cs);
            v = Vector256.ConditionalSelect(Vector256.LessThanOrEqual(cs, Vector256<float>.Zero), Vector256<float>.Zero, v);
            return Vector256.ConditionalSelect(Vector256.GreaterThanOrEqual(cb, one), one, v);
        }
    }

    private readonly partial struct ExclusionMode
    {
        public static Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs) => cb + cs - (Vector256.Create(2f) * cb * cs);
    }

    private readonly partial struct EncodedChannels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Decode(Vector256<int> value) => Vector256.ConvertToSingle(value) / Vector256.Create(255f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> Encode(Vector256<float> value) =>
            Vector256.Clamp(Vector256.ConvertToInt32((value * Vector256.Create(255f)) + Vector256.Create(0.5f)), Vector256<int>.Zero, Vector256.Create(255));
    }

    private readonly partial struct LinearChannels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Decode(Vector256<int> value)
        {
            ReadOnlySpan<float> table = SrgbTransfer.DecodeTable;
            return Vector256.Create(table[value[0]], table[value[1]], table[value[2]], table[value[3]],
                table[value[4]], table[value[5]], table[value[6]], table[value[7]]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<int> Encode(Vector256<float> value)
        {
            ReadOnlySpan<byte> table = SrgbTransfer.EncodeTable;
            Vector256<int> index = SrgbTransfer.EncodeIndex(value);
            return Vector256.Create(table[index[0]], table[index[1]], table[index[2]], table[index[3]],
                table[index[4]], table[index[5]], table[index[6]], table[index[7]]);
        }
    }
}
