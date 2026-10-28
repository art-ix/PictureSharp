using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PictureSharp.Domain.Hdr;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

internal sealed partial class GainMapReconstructor
{
    /// <summary>The eight-pixel groups of <see cref="RowSimd"/> with AVX2, from the start of the row.</summary>
    private int RowAvx2(Rgba32[] basePixels, Rgba128[] output, int y)
    {
        ReadOnlySpan<Rgba32> source = basePixels.AsSpan(y * _width, _width);
        ref float target = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba128, float>(output.AsSpan(y * _width, _width)));
        ReadOnlySpan<float> linear = _linear;
        ReadOnlySpan<Rgba32> map = _map.Pixels.Rgba32;
        bool iso = _p.Kind == HdrGainMapKinds.Iso21496;
        bool scaled = IsScaled;

        // The source position is affine in x: s(x) = s(0) + x · (s(1) − s(0)).
        (int sx0, int sy0) = OrientedSource(0, y);
        (int sx1, int sy1) = OrientedSource(1, y);
        Vector256<int> lane = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7);
        Vector256<float> half = Vector256.Create(0.5f);
        int count = source.Length & ~7;
        for (int x = 0; x < count; x += 8)
        {
            Rgba32 p0 = source[x], p1 = source[x + 1], p2 = source[x + 2], p3 = source[x + 3];
            Rgba32 p4 = source[x + 4], p5 = source[x + 5], p6 = source[x + 6], p7 = source[x + 7];
            Vector256<float> r = Vector256.Create(linear[p0.R], linear[p1.R], linear[p2.R], linear[p3.R],
                linear[p4.R], linear[p5.R], linear[p6.R], linear[p7.R]);
            Vector256<float> g = Vector256.Create(linear[256 + p0.G], linear[256 + p1.G], linear[256 + p2.G], linear[256 + p3.G],
                linear[256 + p4.G], linear[256 + p5.G], linear[256 + p6.G], linear[256 + p7.G]);
            Vector256<float> b = Vector256.Create(linear[512 + p0.B], linear[512 + p1.B], linear[512 + p2.B], linear[512 + p3.B],
                linear[512 + p4.B], linear[512 + p5.B], linear[512 + p6.B], linear[512 + p7.B]);
            if (_toMultiply is { } m)
            {
                (r, g, b) = m.Transform(r, g, b);
            }

            Vector256<int> xs = Vector256.Create(x) + lane;
            Vector256<float> bx = Vector256.ConvertToSingle(Vector256.Create(sx0) + (xs * (sx1 - sx0)));
            Vector256<float> by = Vector256.ConvertToSingle(Vector256.Create(sy0) + (xs * (sy1 - sy0)));
            if (scaled)
            {
                bx = (((bx + half) * Vector256.Create((float)_storedWidth)) / Vector256.Create((float)_orientedWidth)) - half;
                by = (((by + half) * Vector256.Create((float)_storedHeight)) / Vector256.Create((float)_orientedHeight)) - half;
            }

            if (iso)
            {
                Vector256<float> mx = Vector256.Max(bx, Vector256<float>.Zero) / Vector256.Create(_mapScaleX);
                Vector256<float> my = Vector256.Max(by, Vector256<float>.Zero) / Vector256.Create(_mapScaleY);
                GainMapMath.SampleIdw(map, _map.Width, _map.Height, mx, my, _p.ChannelCount,
                    out Vector256<float> gr, out Vector256<float> gg, out Vector256<float> gb);
                Vector256<float> fr = GainMapMath.IsoGainFactor(gr, _p.GainMapMin[0], _p.GainMapMax[0], _p.Gamma[0], _weight);
                Vector256<float> fg = fr, fb = fr;
                if (_p.ChannelCount != 1)
                {
                    fg = GainMapMath.IsoGainFactor(gg, _p.GainMapMin[1], _p.GainMapMax[1], _p.Gamma[1], _weight);
                    fb = GainMapMath.IsoGainFactor(gb, _p.GainMapMin[2], _p.GainMapMax[2], _p.Gamma[2], _weight);
                }

                r = ((r + Vector256.Create(_p.BaseOffset[0])) * fr) - Vector256.Create(_p.AlternateOffset[0]);
                g = ((g + Vector256.Create(_p.BaseOffset[1])) * fg) - Vector256.Create(_p.AlternateOffset[1]);
                b = ((b + Vector256.Create(_p.BaseOffset[2])) * fb) - Vector256.Create(_p.AlternateOffset[2]);
            }
            else
            {
                Vector256<float> mx = ((bx + half) / Vector256.Create(_mapScaleX)) - half;
                Vector256<float> my = ((by + half) / Vector256.Create(_mapScaleY)) - half;
                Vector256<float> gain = GainMapMath.Rec709ToLinear(GainMapMath.SampleBilinear(map, _map.Width, _map.Height, mx, my));
                Vector256<float> factor = Vector256<float>.One + (Vector256.Create(_appleFactor) * gain);
                (r, g, b) = (r * factor, g * factor, b * factor);
            }

            if (_toOutput is { } o)
            {
                (r, g, b) = o.Transform(r, g, b);
            }

            Vector256<float> a = Vector256.Create((float)p0.A, p1.A, p2.A, p3.A, p4.A, p5.A, p6.A, p7.A) / Vector256.Create(255f);
            Vector256<int> ri = r.AsInt32(), gi = g.AsInt32(), bi = b.AsInt32(), ai = a.AsInt32();

            // Each 128-bit half transposes to four pixels: the low halves hold pixels 0–3, the high halves 4–7.
            Simd.Transpose(ref ri, ref gi, ref bi, ref ai);
            ri.GetLower().AsSingle().StoreUnsafe(ref target, (nuint)(x * 4));
            gi.GetLower().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 4));
            bi.GetLower().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 8));
            ai.GetLower().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 12));
            ri.GetUpper().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 16));
            gi.GetUpper().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 20));
            bi.GetUpper().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 24));
            ai.GetUpper().AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 28));
        }

        return count;
    }

}
