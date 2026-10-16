using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Algorithms.Processing;

// Vector forms of the blend: each lane performs the float operations of BlendPixel and Channel in the same order, so
// the bytes are identical to the scalar code. Branches of the blend functions become selects of both results.
internal static partial class Compositor
{
    private partial interface IBlendFunction
    {
        /// <summary>Lane-wise <see cref="Blend(float, float)"/> with the same operations.</summary>
        static abstract Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs);

        /// <summary>Lane-wise <see cref="Blend(float, float)"/> with the same operations.</summary>
        static abstract Vector256<float> Blend(Vector256<float> cb, Vector256<float> cs);
    }

    private partial interface IChannelTransfer
    {
        /// <summary>Lane-wise <see cref="Decode(byte)"/> of values 0–255.</summary>
        static abstract Vector128<float> Decode(Vector128<int> value);

        /// <summary>Lane-wise <see cref="Encode(float)"/>, as ints 0–255.</summary>
        static abstract Vector128<int> Encode(Vector128<float> value);

        /// <summary>Lane-wise <see cref="Decode(byte)"/> of values 0–255.</summary>
        static abstract Vector256<float> Decode(Vector256<int> value);

        /// <summary>Lane-wise <see cref="Encode(float)"/>, as ints 0–255.</summary>
        static abstract Vector256<int> Encode(Vector256<float> value);
    }

    /// <summary>Blends whole groups of eight (AVX2) and four pixels of a row; returns the pixels processed.</summary>
    private static int BlendRowSimd<TChannels, TMode>(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        if (!Vector128.IsHardwareAccelerated || !BitConverter.IsLittleEndian)
        {
            return 0;
        }

        int done = Avx2.IsSupported ? BlendRowAvx2<TChannels, TMode>(dstRow, srcRow, opacity) : 0;
        return BlendRowVector128<TChannels, TMode>(dstRow, srcRow, opacity, done);
    }

    private static int BlendRowVector128<TChannels, TMode>(Span<Rgba32> dstRow, ReadOnlySpan<Rgba32> srcRow, float opacity, int start)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        ref uint d = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(dstRow));
        ref uint s = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba32, uint>(srcRow));
        Vector128<float> unit = Vector128.Create(255f);
        Vector128<float> one = Vector128<float>.One;
        Vector128<uint> bytes = Vector128.Create(0xFFu);
        int count = start + ((dstRow.Length - start) & ~3);
        for (int i = start; i < count; i += 4)
        {
            Vector128<uint> sp = Vector128.LoadUnsafe(ref s, (nuint)i);
            Vector128<uint> sa = sp >> 24;
            if (sa == Vector128<uint>.Zero)
            {
                continue;
            }

            Vector128<uint> dp = Vector128.LoadUnsafe(ref d, (nuint)i);
            Vector128<float> ab = Vector128.ConvertToSingle((dp >> 24).AsInt32()) / unit;
            Vector128<float> alphaS = (Vector128.ConvertToSingle(sa.AsInt32()) / unit) * Vector128.Create(opacity);
            Vector128<float> backdropWeight = ab * (one - alphaS);
            Vector128<float> ao = alphaS + backdropWeight;
            Vector128<int> r = Channel<TChannels, TMode>(dp & bytes, sp & bytes, ab, alphaS, backdropWeight, ao);
            Vector128<int> g = Channel<TChannels, TMode>((dp >> 8) & bytes, (sp >> 8) & bytes, ab, alphaS, backdropWeight, ao);
            Vector128<int> b = Channel<TChannels, TMode>((dp >> 16) & bytes, (sp >> 16) & bytes, ab, alphaS, backdropWeight, ao);
            Vector128<int> a = Vector128.ConvertToInt32((ao * unit) + Vector128.Create(0.5f));
            Vector128<uint> blended = (r | (g << 8) | (b << 16) | (a << 24)).AsUInt32();
            Vector128.ConditionalSelect(Vector128.Equals(sa, Vector128<uint>.Zero), dp, blended).StoreUnsafe(ref d, (nuint)i);
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Channel<TChannels, TMode>(Vector128<uint> backdrop, Vector128<uint> source, Vector128<float> ab,
        Vector128<float> alphaS, Vector128<float> backdropWeight, Vector128<float> ao)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        Vector128<float> cb = TChannels.Decode(backdrop.AsInt32());
        Vector128<float> cs = TChannels.Decode(source.AsInt32());
        Vector128<float> mixed = ((Vector128<float>.One - ab) * cs) + (ab * TMode.Blend(cb, cs));
        return TChannels.Encode(((alphaS * mixed) + (backdropWeight * cb)) / ao);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> Screen(Vector128<float> cb, Vector128<float> cs) => cb + cs - (cb * cs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<float> HardLight(Vector128<float> cb, Vector128<float> cs) =>
        Vector128.ConditionalSelect(Vector128.LessThanOrEqual(cs, Vector128.Create(0.5f)),
            cb * Vector128.Create(2f) * cs, Screen(cb, (Vector128.Create(2f) * cs) - Vector128<float>.One));

    private readonly partial struct NormalMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => cs;
    }

    private readonly partial struct MultiplyMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => cb * cs;
    }

    private readonly partial struct ScreenMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => Screen(cb, cs);
    }

    private readonly partial struct OverlayMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => HardLight(cs, cb);
    }

    private readonly partial struct DarkenMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => Vector128.Min(cb, cs);
    }

    private readonly partial struct LightenMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => Vector128.Max(cb, cs);
    }

    private readonly partial struct DifferenceMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => Vector128.Abs(cb - cs);
    }

    private readonly partial struct HardLightMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => HardLight(cb, cs);
    }

    private readonly partial struct SoftLightMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs)
        {
            Vector128<float> one = Vector128<float>.One, two = Vector128.Create(2f);
            Vector128<float> dark = cb - ((one - (two * cs)) * cb * (one - cb));
            Vector128<float> polynomial = ((((Vector128.Create(16f) * cb) - Vector128.Create(12f)) * cb) + Vector128.Create(4f)) * cb;
            Vector128<float> d = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(cb, Vector128.Create(0.25f)), polynomial, Vector128.Sqrt(cb));
            Vector128<float> light = cb + (((two * cs) - one) * (d - cb));
            return Vector128.ConditionalSelect(Vector128.LessThanOrEqual(cs, Vector128.Create(0.5f)), dark, light);
        }
    }

    private readonly partial struct ColorDodgeMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs)
        {
            Vector128<float> one = Vector128<float>.One;
            Vector128<float> v = Vector128.Min(one, cb / (one - cs));
            v = Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(cs, one), one, v);
            return Vector128.ConditionalSelect(Vector128.Equals(cb, Vector128<float>.Zero), Vector128<float>.Zero, v);
        }
    }

    private readonly partial struct ColorBurnMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs)
        {
            Vector128<float> one = Vector128<float>.One;
            Vector128<float> v = one - Vector128.Min(one, (one - cb) / cs);
            v = Vector128.ConditionalSelect(Vector128.LessThanOrEqual(cs, Vector128<float>.Zero), Vector128<float>.Zero, v);
            return Vector128.ConditionalSelect(Vector128.GreaterThanOrEqual(cb, one), one, v);
        }
    }

    private readonly partial struct ExclusionMode
    {
        public static Vector128<float> Blend(Vector128<float> cb, Vector128<float> cs) => cb + cs - (Vector128.Create(2f) * cb * cs);
    }

    private readonly partial struct EncodedChannels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Decode(Vector128<int> value) => Vector128.ConvertToSingle(value) / Vector128.Create(255f);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> Encode(Vector128<float> value) =>
            Vector128.Clamp(Vector128.ConvertToInt32((value * Vector128.Create(255f)) + Vector128.Create(0.5f)), Vector128<int>.Zero, Vector128.Create(255));
    }

    private readonly partial struct LinearChannels
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<float> Decode(Vector128<int> value)
        {
            ReadOnlySpan<float> table = SrgbTransfer.DecodeTable;
            return Vector128.Create(table[value[0]], table[value[1]], table[value[2]], table[value[3]]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<int> Encode(Vector128<float> value)
        {
            ReadOnlySpan<byte> table = SrgbTransfer.EncodeTable;
            Vector128<int> index = SrgbTransfer.EncodeIndex(value);
            return Vector128.Create(table[index[0]], table[index[1]], table[index[2]], table[index[3]]);
        }
    }
}
