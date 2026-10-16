using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Domain.Hdr;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Hdr;

internal sealed partial class GainMapReconstructor
{
    /// <summary>
    /// Reconstructs whole groups of eight (AVX2) and four pixels of row <paramref name="y"/> with the operations of
    /// <see cref="RowScalar"/> in the same order, so the floats are identical; returns the pixels written.
    /// </summary>
    internal int RowSimd(Rgba32[] basePixels, Rgba128[] output, int y)
    {
        int done = Avx2.IsSupported ? RowAvx2(basePixels, output, y) : 0;
        return Simd.IsSupported128 ? RowVector128(basePixels, output, y, done) : done;
    }

    /// <summary>The four-pixel groups of <see cref="RowSimd"/> from pixel <paramref name="start"/> on.</summary>
    private int RowVector128(Rgba32[] basePixels, Rgba128[] output, int y, int start)
    {
        ReadOnlySpan<Rgba32> source = basePixels.AsSpan(y * _width, _width);
        ref float target = ref MemoryMarshal.GetReference(MemoryMarshal.Cast<Rgba128, float>(output.AsSpan(y * _width, _width)));
        ReadOnlySpan<float> linear = _linear;
        ReadOnlySpan<Rgba32> map = _map.Pixels;
        bool iso = _p.Kind == HdrGainMapKinds.Iso21496;
        bool scaled = IsScaled;

        // The source position is affine in x: s(x) = s(0) + x · (s(1) − s(0)).
        (int sx0, int sy0) = OrientedSource(0, y);
        (int sx1, int sy1) = OrientedSource(1, y);
        Vector128<int> lane = Vector128.Create(0, 1, 2, 3);
        Vector128<float> half = Vector128.Create(0.5f);
        int count = start + ((source.Length - start) & ~3);
        for (int x = start; x < count; x += 4)
        {
            Rgba32 p0 = source[x], p1 = source[x + 1], p2 = source[x + 2], p3 = source[x + 3];
            Vector128<float> r = Vector128.Create(linear[p0.R], linear[p1.R], linear[p2.R], linear[p3.R]);
            Vector128<float> g = Vector128.Create(linear[256 + p0.G], linear[256 + p1.G], linear[256 + p2.G], linear[256 + p3.G]);
            Vector128<float> b = Vector128.Create(linear[512 + p0.B], linear[512 + p1.B], linear[512 + p2.B], linear[512 + p3.B]);
            if (_toMultiply is { } m)
            {
                (r, g, b) = Transform(m, r, g, b);
            }

            Vector128<int> xs = Vector128.Create(x) + lane;
            Vector128<float> bx = Vector128.ConvertToSingle(Vector128.Create(sx0) + (xs * (sx1 - sx0)));
            Vector128<float> by = Vector128.ConvertToSingle(Vector128.Create(sy0) + (xs * (sy1 - sy0)));
            if (scaled)
            {
                bx = (((bx + half) * Vector128.Create((float)_storedWidth)) / Vector128.Create((float)_orientedWidth)) - half;
                by = (((by + half) * Vector128.Create((float)_storedHeight)) / Vector128.Create((float)_orientedHeight)) - half;
            }

            if (iso)
            {
                Vector128<float> mx = Vector128.Max(bx, Vector128<float>.Zero) / Vector128.Create(_mapScaleX);
                Vector128<float> my = Vector128.Max(by, Vector128<float>.Zero) / Vector128.Create(_mapScaleY);
                GainMapMath.SampleIdw(map, _map.Width, _map.Height, mx, my, _p.ChannelCount,
                    out Vector128<float> gr, out Vector128<float> gg, out Vector128<float> gb);
                Vector128<float> fr = GainMapMath.IsoGainFactor(gr, _p.GainMapMin[0], _p.GainMapMax[0], _p.Gamma[0], _weight);
                Vector128<float> fg = fr, fb = fr;
                if (_p.ChannelCount != 1)
                {
                    fg = GainMapMath.IsoGainFactor(gg, _p.GainMapMin[1], _p.GainMapMax[1], _p.Gamma[1], _weight);
                    fb = GainMapMath.IsoGainFactor(gb, _p.GainMapMin[2], _p.GainMapMax[2], _p.Gamma[2], _weight);
                }

                r = ((r + Vector128.Create(_p.BaseOffset[0])) * fr) - Vector128.Create(_p.AlternateOffset[0]);
                g = ((g + Vector128.Create(_p.BaseOffset[1])) * fg) - Vector128.Create(_p.AlternateOffset[1]);
                b = ((b + Vector128.Create(_p.BaseOffset[2])) * fb) - Vector128.Create(_p.AlternateOffset[2]);
            }
            else
            {
                Vector128<float> mx = ((bx + half) / Vector128.Create(_mapScaleX)) - half;
                Vector128<float> my = ((by + half) / Vector128.Create(_mapScaleY)) - half;
                Vector128<float> gain = GainMapMath.Rec709ToLinear(GainMapMath.SampleBilinear(map, _map.Width, _map.Height, mx, my));
                Vector128<float> factor = Vector128<float>.One + (Vector128.Create(_appleFactor) * gain);
                (r, g, b) = (r * factor, g * factor, b * factor);
            }

            if (_toOutput is { } o)
            {
                (r, g, b) = Transform(o, r, g, b);
            }

            Vector128<float> a = Vector128.Create((float)p0.A, p1.A, p2.A, p3.A) / Vector128.Create(255f);
            Vector128<int> ri = r.AsInt32(), gi = g.AsInt32(), bi = b.AsInt32(), ai = a.AsInt32();
            Simd.Transpose(ref ri, ref gi, ref bi, ref ai);
            ri.AsSingle().StoreUnsafe(ref target, (nuint)(x * 4));
            gi.AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 4));
            bi.AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 8));
            ai.AsSingle().StoreUnsafe(ref target, (nuint)((x * 4) + 12));
        }

        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (Vector128<float>, Vector128<float>, Vector128<float>) Transform(float[] m, Vector128<float> r, Vector128<float> g, Vector128<float> b) =>
        ((Vector128.Create(m[0]) * r) + (Vector128.Create(m[1]) * g) + (Vector128.Create(m[2]) * b),
         (Vector128.Create(m[3]) * r) + (Vector128.Create(m[4]) * g) + (Vector128.Create(m[5]) * b),
         (Vector128.Create(m[6]) * r) + (Vector128.Create(m[7]) * g) + (Vector128.Create(m[8]) * b));
}
