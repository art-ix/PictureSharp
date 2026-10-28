using System.Drawing;
using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Compositor
{
    /// <summary>
    /// Draws <paramref name="src"/> over <paramref name="dst"/> with a separable blend mode of W3C Compositing and
    /// Blending Level 1: Cs' = (1 − αb) · Cs + αb · B(Cb, Cs), then source-over with the source alpha scaled by
    /// <paramref name="opacity"/>, on straight alpha in float precision (tests/assets/graphics_reference.py blend).
    /// With <paramref name="linearLight"/> the colors are decoded from sRGB before blending and encoded afterwards.
    /// A transparent source pixel leaves the destination untouched. <paramref name="vectorize"/> = false keeps to the
    /// scalar code, which the vector code equals bit for bit (tests compare them).
    /// </summary>
    public static void DrawBlended(ImageFrame dst, ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Point at, float opacity,
        BlendMode mode, bool linearLight, bool vectorize = true, CancellationToken cancellationToken = default)
    {
        if (linearLight)
        {
            DrawBlended<LinearChannels>(dst, src, srcWidth, srcHeight, at, opacity, mode, vectorize, cancellationToken);
        }
        else
        {
            DrawBlended<EncodedChannels>(dst, src, srcWidth, srcHeight, at, opacity, mode, vectorize, cancellationToken);
        }
    }

    // One specialization per mode keeps the mode switch out of the pixel loop.
    private static void DrawBlended<TChannels>(ImageFrame dst, ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Point at, float opacity,
        BlendMode mode, bool vectorize, CancellationToken cancellationToken)
        where TChannels : struct, IChannelTransfer
    {
        switch (mode)
        {
            case BlendMode.Multiply: DrawBlended<TChannels, MultiplyMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Screen: DrawBlended<TChannels, ScreenMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Overlay: DrawBlended<TChannels, OverlayMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Darken: DrawBlended<TChannels, DarkenMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Lighten: DrawBlended<TChannels, LightenMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Difference: DrawBlended<TChannels, DifferenceMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.HardLight: DrawBlended<TChannels, HardLightMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.SoftLight: DrawBlended<TChannels, SoftLightMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.ColorDodge: DrawBlended<TChannels, ColorDodgeMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.ColorBurn: DrawBlended<TChannels, ColorBurnMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            case BlendMode.Exclusion: DrawBlended<TChannels, ExclusionMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
            default: DrawBlended<TChannels, NormalMode>(dst, src, srcWidth, srcHeight, at, opacity, vectorize, cancellationToken); break;
        }
    }

    private static void DrawBlended<TChannels, TMode>(ImageFrame dst, ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Point at, float opacity,
        bool vectorize, CancellationToken cancellationToken)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        int x0 = Math.Max(0, at.X);
        int y0 = Math.Max(0, at.Y);
        int x1 = (int)Math.Min(dst.Width, (long)at.X + srcWidth);
        int y1 = (int)Math.Min(dst.Height, (long)at.Y + srcHeight);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        int n = x1 - x0;
        for (int y = y0; y < y1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<Rgba32> dstRow = dst.PixelRow(y).Slice(x0, n);
            ReadOnlySpan<Rgba32> srcRow = src.Slice(((y - at.Y) * srcWidth) + (x0 - at.X), n);
            int done = vectorize ? BlendRowSimd<TChannels, TMode>(dstRow, srcRow, opacity) : 0;
            for (int x = done; x < n; x++)
            {
                Rgba32 s = srcRow[x];
                if (s.A != 0)
                {
                    dstRow[x] = BlendPixel<TChannels, TMode>(dstRow[x], s, opacity);
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba32 BlendPixel<TChannels, TMode>(Rgba32 d, Rgba32 s, float opacity)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        ReadOnlySpan<float> unit = UnitTable.Values;
        float ab = unit[d.A];
        float alphaS = unit[s.A] * opacity;
        float backdropWeight = ab * (1f - alphaS);
        float ao = alphaS + backdropWeight;
        return new Rgba32(
            Channel<TChannels, TMode>(d.R, s.R, ab, alphaS, backdropWeight, ao),
            Channel<TChannels, TMode>(d.G, s.G, ab, alphaS, backdropWeight, ao),
            Channel<TChannels, TMode>(d.B, s.B, ab, alphaS, backdropWeight, ao),
            (byte)((ao * 255f) + 0.5f));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Channel<TChannels, TMode>(byte backdrop, byte source, float ab, float alphaS, float backdropWeight, float ao)
        where TChannels : struct, IChannelTransfer
        where TMode : struct, IBlendFunction
    {
        float cb = TChannels.Decode(backdrop);
        float cs = TChannels.Decode(source);
        float mixed = ((1f - ab) * cs) + (ab * TMode.Blend(cb, cs));
        return TChannels.Encode(((alphaS * mixed) + (backdropWeight * cb)) / ao);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Screen(float cb, float cs) => cb + cs - (cb * cs);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HardLight(float cb, float cs) => cs <= 0.5f ? cb * 2f * cs : Screen(cb, (2f * cs) - 1f);

    /// <summary>A separable blend function B(Cb, Cs) on values in [0, 1].</summary>
    private partial interface IBlendFunction
    {
        static abstract float Blend(float cb, float cs);

    }

    // port: W3C Compositing and Blending Level 1, 10.1 separable blend modes
    private readonly partial struct NormalMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => cs;
    }

    private readonly partial struct MultiplyMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => cb * cs;
    }

    private readonly partial struct ScreenMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => Screen(cb, cs);
    }

    private readonly partial struct OverlayMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => HardLight(cs, cb);
    }

    private readonly partial struct DarkenMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => MathF.Min(cb, cs);
    }

    private readonly partial struct LightenMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => MathF.Max(cb, cs);
    }

    private readonly partial struct DifferenceMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => MathF.Abs(cb - cs);
    }

    private readonly partial struct HardLightMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => HardLight(cb, cs);
    }

    private readonly partial struct SoftLightMode : IBlendFunction
    {
        public static float Blend(float cb, float cs)
        {
            if (cs <= 0.5f)
            {
                return cb - ((1f - (2f * cs)) * cb * (1f - cb));
            }

            float d = cb <= 0.25f ? ((((16f * cb) - 12f) * cb) + 4f) * cb : MathF.Sqrt(cb);
            return cb + (((2f * cs) - 1f) * (d - cb));
        }
    }

    private readonly partial struct ColorDodgeMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => cb == 0f ? 0f : cs >= 1f ? 1f : MathF.Min(1f, cb / (1f - cs));
    }

    private readonly partial struct ColorBurnMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => cb >= 1f ? 1f : cs <= 0f ? 0f : 1f - MathF.Min(1f, (1f - cb) / cs);
    }

    private readonly partial struct ExclusionMode : IBlendFunction
    {
        public static float Blend(float cb, float cs) => cb + cs - (2f * cb * cs);
    }

    /// <summary>Conversion between 8-bit channel values and the space colors are blended in.</summary>
    private partial interface IChannelTransfer
    {
        static abstract float Decode(byte value);

        static abstract byte Encode(float value);

    }

    private readonly partial struct EncodedChannels : IChannelTransfer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Decode(byte value) => UnitTable.Values[value];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Encode(float value) => (byte)Math.Clamp((int)((value * 255f) + 0.5f), 0, 255);
    }

    private readonly partial struct LinearChannels : IChannelTransfer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Decode(byte value) => SrgbTransfer.Decode(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte Encode(float value) => SrgbTransfer.Encode(value);
    }

    /// <summary>v / 255 for every 8-bit value, exactly as the division would give.</summary>
    private static class UnitTable
    {
        public static readonly float[] Values = Build();

        private static float[] Build()
        {
            var table = new float[256];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = i / 255f;
            }

            return table;
        }
    }
}
