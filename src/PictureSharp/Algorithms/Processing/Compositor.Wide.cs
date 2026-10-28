using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Compositor
{
    /// <summary>
    /// DrawImage of <paramref name="source"/> onto one frame. Two 8-bit buffers use the 8-bit ports; any 16-bit buffer
    /// selects the float blend of <c>DrawWide</c>, which keeps the source at its precision until the destination write.
    /// </summary>
    public readonly struct DrawFrame(ImageFrame frame, PixelBuffer source, int srcWidth, int srcHeight, Point at, DrawImageOptions options,
        CancellationToken cancellationToken) : IPixelOperation<bool>
    {
        public bool Run<TDst>() where TDst : unmanaged, IPixelOps<TDst>
        {
            var operation = new DrawSource<TDst>(frame, source, srcWidth, srcHeight, at, options, cancellationToken);
            return PixelDispatch.Run<DrawSource<TDst>, bool>(source.PixelType, ref operation);
        }
    }

    private readonly struct DrawSource<TDst>(ImageFrame frame, PixelBuffer source, int srcWidth, int srcHeight, Point at, DrawImageOptions options,
        CancellationToken cancellationToken) : IPixelOperation<bool>
        where TDst : unmanaged, IPixelOps<TDst>
    {
        public bool Run<TSrc>() where TSrc : unmanaged, IPixelOps<TSrc>
        {
            if (typeof(TDst) == typeof(Rgba32) && typeof(TSrc) == typeof(Rgba32))
            {
                Rgba32[] src = source.As<Rgba32>();
                if (options.BlendMode != BlendMode.Normal)
                {
                    DrawBlended(frame, src, srcWidth, srcHeight, at, options.Opacity, options.BlendMode, options.LinearLight, cancellationToken: cancellationToken);
                }
                else if (options.LinearLight)
                {
                    DrawOverLinear(frame, src, srcWidth, srcHeight, at, options.Opacity, cancellationToken: cancellationToken);
                }
                else
                {
                    DrawOver(frame, src, srcWidth, srcHeight, at, options.Opacity, cancellationToken: cancellationToken);
                }
            }
            else
            {
                DrawWide(frame.Buffer.As<TDst>(), frame.Width, frame.Height, source.As<TSrc>(), srcWidth, srcHeight, at, options.Opacity,
                    options.BlendMode, options.LinearLight, cancellationToken);
            }

            return true;
        }
    }

    /// <summary>
    /// The blend of <see cref="DrawBlended"/> (source-over for <see cref="BlendMode.Normal"/>) on straight working
    /// values in float, with the source alpha scaled by <paramref name="opacity"/> unrounded; each result is rounded
    /// once to the destination depth. A transparent source pixel leaves the destination untouched.
    /// </summary>
    public static void DrawWide<TDst, TSrc>(TDst[] dst, int dstWidth, int dstHeight, TSrc[] src, int srcWidth, int srcHeight, Point at,
        float opacity, BlendMode mode, bool linearLight, CancellationToken cancellationToken = default)
        where TDst : unmanaged, IPixelOps<TDst>
        where TSrc : unmanaged, IPixelOps<TSrc>
    {
        var target = new Region<TDst, TSrc>(dst, dstWidth, dstHeight, src, srcWidth, srcHeight, at, opacity, cancellationToken);
        if (linearLight)
        {
            DrawWide<TDst, TSrc, LinearUnit>(target, mode);
        }
        else
        {
            DrawWide<TDst, TSrc, EncodedUnit>(target, mode);
        }
    }

    private static void DrawWide<TDst, TSrc, TChannels>(Region<TDst, TSrc> target, BlendMode mode)
        where TDst : unmanaged, IPixelOps<TDst>
        where TSrc : unmanaged, IPixelOps<TSrc>
        where TChannels : struct, IUnitTransfer
    {
        switch (mode)
        {
            case BlendMode.Multiply: DrawWide<TDst, TSrc, TChannels, MultiplyMode>(target); break;
            case BlendMode.Screen: DrawWide<TDst, TSrc, TChannels, ScreenMode>(target); break;
            case BlendMode.Overlay: DrawWide<TDst, TSrc, TChannels, OverlayMode>(target); break;
            case BlendMode.Darken: DrawWide<TDst, TSrc, TChannels, DarkenMode>(target); break;
            case BlendMode.Lighten: DrawWide<TDst, TSrc, TChannels, LightenMode>(target); break;
            case BlendMode.Difference: DrawWide<TDst, TSrc, TChannels, DifferenceMode>(target); break;
            case BlendMode.HardLight: DrawWide<TDst, TSrc, TChannels, HardLightMode>(target); break;
            case BlendMode.SoftLight: DrawWide<TDst, TSrc, TChannels, SoftLightMode>(target); break;
            case BlendMode.ColorDodge: DrawWide<TDst, TSrc, TChannels, ColorDodgeMode>(target); break;
            case BlendMode.ColorBurn: DrawWide<TDst, TSrc, TChannels, ColorBurnMode>(target); break;
            case BlendMode.Exclusion: DrawWide<TDst, TSrc, TChannels, ExclusionMode>(target); break;
            default: DrawWide<TDst, TSrc, TChannels, NormalMode>(target); break;
        }
    }

    private static void DrawWide<TDst, TSrc, TChannels, TMode>(Region<TDst, TSrc> r)
        where TDst : unmanaged, IPixelOps<TDst>
        where TSrc : unmanaged, IPixelOps<TSrc>
        where TChannels : struct, IUnitTransfer
        where TMode : struct, IBlendFunction
    {
        int x0 = Math.Max(0, r.At.X);
        int y0 = Math.Max(0, r.At.Y);
        int x1 = (int)Math.Min(r.DstWidth, (long)r.At.X + r.SrcWidth);
        int y1 = (int)Math.Min(r.DstHeight, (long)r.At.Y + r.SrcHeight);
        if (x0 >= x1 || y0 >= y1)
        {
            return;
        }

        int n = x1 - x0;
        Vector4[] line = ArrayPool<Vector4>.Shared.Rent(n);
        try
        {
            Span<Vector4> source = line.AsSpan(0, n);
            for (int y = y0; y < y1; y++)
            {
                r.CancellationToken.ThrowIfCancellationRequested();
                Span<TDst> dstRow = r.Dst.AsSpan((y * r.DstWidth) + x0, n);
                UnitRows.Load<TSrc, TChannels>(r.Src.AsSpan(((y - r.At.Y) * r.SrcWidth) + (x0 - r.At.X), n), source);
                for (int x = 0; x < n; x++)
                {
                    Vector4 s = source[x];
                    if (s.W != 0f)
                    {
                        dstRow[x] = TChannels.Store<TDst>(BlendWide<TMode>(TChannels.Load(dstRow[x]), s, r.Opacity));
                    }
                }
            }
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(line);
        }
    }

    /// <summary>The straight-alpha result of <see cref="BlendPixel"/> before rounding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 BlendWide<TMode>(Vector4 d, Vector4 s, float opacity) where TMode : struct, IBlendFunction
    {
        float ab = d.W;
        float alphaS = s.W * opacity;
        float backdropWeight = ab * (1f - alphaS);
        float ao = alphaS + backdropWeight;
        return new Vector4(
            ChannelWide<TMode>(d.X, s.X, ab, alphaS, backdropWeight, ao),
            ChannelWide<TMode>(d.Y, s.Y, ab, alphaS, backdropWeight, ao),
            ChannelWide<TMode>(d.Z, s.Z, ab, alphaS, backdropWeight, ao),
            ao);
    }

    /// <summary>Source-over of straight working values, as <see cref="Over"/> without its 8-bit rounding.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 OverWide(Vector4 d, Vector4 s) => s.W == 0f ? d : BlendWide<NormalMode>(d, s, 1f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float ChannelWide<TMode>(float cb, float cs, float ab, float alphaS, float backdropWeight, float ao)
        where TMode : struct, IBlendFunction
    {
        float mixed = ((1f - ab) * cs) + (ab * TMode.Blend(cb, cs));
        return ((alphaS * mixed) + (backdropWeight * cb)) / ao;
    }

    private readonly record struct Region<TDst, TSrc>(TDst[] Dst, int DstWidth, int DstHeight, TSrc[] Src, int SrcWidth, int SrcHeight,
        Point At, float Opacity, CancellationToken CancellationToken);
}
