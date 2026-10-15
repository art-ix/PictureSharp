using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

// ARM64 in-loop filters: the 16 positions along an edge are filtered as two halves of eight 16-bit lanes with the
// same clamps as the AVX2 code, so the result equals the scalar filters for every input.
internal static partial class Vp8Dsp
{
    private static void FilterAcrossRowsArm(byte[] a, int ao, byte[] b, int bo, int stride, EdgeFilter filter, int thresh, int ithresh, int hevThresh)
    {
        Vector128<byte> Row(int k) => Vector128.Create(Load8(a, ao + (k * stride)), Load8(b, bo + (k * stride))).AsByte();
        void Store(int k, Vector128<byte> value)
        {
            Store8(a, ao + (k * stride), value.AsUInt64().GetElement(0));
            Store8(b, bo + (k * stride), value.AsUInt64().GetElement(1));
        }

        if (filter == EdgeFilter.Simple)
        {
            Vector128<byte> sp0 = Row(-1), sq0 = Row(0);
            FilterSimpleArm(Row(-2), ref sp0, ref sq0, Row(1), thresh);
            Store(-1, sp0);
            Store(0, sq0);
            return;
        }

        Vector128<byte> p2 = Row(-3), p1 = Row(-2), p0 = Row(-1), q0 = Row(0), q1 = Row(1), q2 = Row(2);
        FilterComplexArm(Row(-4), ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, Row(3), filter == EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        if (filter == EdgeFilter.MacroblockEdge)
        {
            Store(-3, p2);
            Store(2, q2);
        }

        Store(-2, p1);
        Store(-1, p0);
        Store(0, q0);
        Store(1, q1);
    }

    private static void FilterAcrossColumnsArm(byte[] a, int ao, byte[] b, int bo, int stride, EdgeFilter filter, int thresh, int ithresh, int hevThresh)
    {
        Load16x8(a, ao - 4, b, bo - 4, stride, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
            out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7);
        if (filter == EdgeFilter.Simple) FilterSimpleArm(c2, ref c3, ref c4, c5, thresh);
        else FilterComplexArm(c0, ref c1, ref c2, ref c3, ref c4, ref c5, ref c6, c7, filter == EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        Store16x8(a, ao - 4, b, bo - 4, stride, c0, c1, c2, c3, c4, c5, c6, c7);
    }

    // Widens the 16 positions into two halves, filters each and narrows back.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterSimpleArm(Vector128<byte> p1, ref Vector128<byte> p0, ref Vector128<byte> q0, Vector128<byte> q1, int thresh)
    {
        Vector128<short> lp0 = Low(p0), lq0 = Low(q0), hp0 = High(p0), hq0 = High(q0);
        SimpleFilter(Low(p1), ref lp0, ref lq0, Low(q1), thresh);
        SimpleFilter(High(p1), ref hp0, ref hq0, High(q1), thresh);
        p0 = Simd.PackUnsignedSaturate(lp0, hp0);
        q0 = Simd.PackUnsignedSaturate(lq0, hq0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void FilterComplexArm(Vector128<byte> p3, ref Vector128<byte> p2, ref Vector128<byte> p1, ref Vector128<byte> p0,
        ref Vector128<byte> q0, ref Vector128<byte> q1, ref Vector128<byte> q2, Vector128<byte> q3,
        bool macroblockEdge, int thresh, int ithresh, int hevThresh)
    {
        Vector128<short> lp2 = Low(p2), lp1 = Low(p1), lp0 = Low(p0), lq0 = Low(q0), lq1 = Low(q1), lq2 = Low(q2);
        Vector128<short> hp2 = High(p2), hp1 = High(p1), hp0 = High(p0), hq0 = High(q0), hq1 = High(q1), hq2 = High(q2);
        ComplexFilter(Low(p3), ref lp2, ref lp1, ref lp0, ref lq0, ref lq1, ref lq2, Low(q3), macroblockEdge, thresh, ithresh, hevThresh);
        ComplexFilter(High(p3), ref hp2, ref hp1, ref hp0, ref hq0, ref hq1, ref hq2, High(q3), macroblockEdge, thresh, ithresh, hevThresh);
        p2 = Simd.PackUnsignedSaturate(lp2, hp2);
        p1 = Simd.PackUnsignedSaturate(lp1, hp1);
        p0 = Simd.PackUnsignedSaturate(lp0, hp0);
        q0 = Simd.PackUnsignedSaturate(lq0, hq0);
        q1 = Simd.PackUnsignedSaturate(lq1, hq1);
        q2 = Simd.PackUnsignedSaturate(lq2, hq2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Low(Vector128<byte> v) => Vector128.WidenLower(v).AsInt16();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> High(Vector128<byte> v) => Vector128.WidenUpper(v).AsInt16();

    // port: NeedsFilter_C + DoFilter2_C on every lane
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SimpleFilter(Vector128<short> p1, ref Vector128<short> p0, ref Vector128<short> q0, Vector128<short> q1, int thresh)
    {
        Vector128<short> needs = Vector128.LessThanOrEqual((Vector128.Abs(p0 - q0) << 2) + Vector128.Abs(p1 - q1), Vector128.Create((short)((2 * thresh) + 1)));
        DoFilter2(p1, p0, q0, q1, out Vector128<short> np0, out Vector128<short> nq0);
        p0 = Vector128.ConditionalSelect(needs, np0, p0);
        q0 = Vector128.ConditionalSelect(needs, nq0, q0);
    }

    // port: FilterLoop26_C (macroblock edges) and FilterLoop24_C (inner edges) on every lane
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComplexFilter(Vector128<short> p3, ref Vector128<short> p2, ref Vector128<short> p1, ref Vector128<short> p0,
        ref Vector128<short> q0, ref Vector128<short> q1, ref Vector128<short> q2, Vector128<short> q3,
        bool macroblockEdge, int thresh, int ithresh, int hevThresh)
    {
        // NeedsFilter2_C
        Vector128<short> limit = Vector128.Create((short)ithresh);
        Vector128<short> edge = (Vector128.Abs(p0 - q0) << 2) + Vector128.Abs(p1 - q1);
        Vector128<short> innerMax = Vector128.Max(Vector128.Max(Vector128.Abs(p3 - p2), Vector128.Abs(p2 - p1)), Vector128.Abs(q3 - q2));
        Vector128<short> p1p0 = Vector128.Abs(p1 - p0), q1q0 = Vector128.Abs(q1 - q0);
        innerMax = Vector128.Max(Vector128.Max(innerMax, Vector128.Abs(q2 - q1)), Vector128.Max(p1p0, q1q0));
        Vector128<short> needs = Vector128.LessThanOrEqual(edge, Vector128.Create((short)((2 * thresh) + 1))) & Vector128.LessThanOrEqual(innerMax, limit);
        if (needs == Vector128<short>.Zero) return;

        // Hev
        Vector128<short> hev = Vector128.GreaterThan(Vector128.Max(p1p0, q1q0), Vector128.Create((short)hevThresh));
        Vector128<short> useFilter2 = needs & hev, useWide = Vector128.AndNot(needs, hev);
        DoFilter2(p1, p0, q0, q1, out Vector128<short> f2p0, out Vector128<short> f2q0);
        Vector128<short> wp1, wp0, wq0, wq1;
        if (macroblockEdge)
        {
            // DoFilter6_C
            Vector128<short> a = Clamp(Diff3(p0, q0) + Clamp(p1 - q1, -128, 127), -128, 127);
            Vector128<short> rounder = Vector128.Create((short)63);
            Vector128<short> a1 = ((a * 27) + rounder) >> 7, a2 = ((a * 18) + rounder) >> 7, a3 = ((a * 9) + rounder) >> 7;
            p2 = Vector128.ConditionalSelect(useWide, Clip255(p2 + a3), p2);
            q2 = Vector128.ConditionalSelect(useWide, Clip255(q2 - a3), q2);
            wp1 = Clip255(p1 + a2);
            wp0 = Clip255(p0 + a1);
            wq0 = Clip255(q0 - a1);
            wq1 = Clip255(q1 - a2);
        }
        else
        {
            // DoFilter4_C
            Vector128<short> a = Diff3(p0, q0);
            Vector128<short> a1 = Clamp((a + Vector128.Create((short)4)) >> 3, -16, 15);
            Vector128<short> a2 = Clamp((a + Vector128.Create((short)3)) >> 3, -16, 15);
            Vector128<short> a3 = (a1 + Vector128<short>.One) >> 1;
            wp1 = Clip255(p1 + a3);
            wp0 = Clip255(p0 + a2);
            wq0 = Clip255(q0 - a1);
            wq1 = Clip255(q1 - a3);
        }

        p1 = Vector128.ConditionalSelect(useWide, wp1, p1);
        q1 = Vector128.ConditionalSelect(useWide, wq1, q1);
        p0 = Vector128.ConditionalSelect(useFilter2, f2p0, Vector128.ConditionalSelect(useWide, wp0, p0));
        q0 = Vector128.ConditionalSelect(useFilter2, f2q0, Vector128.ConditionalSelect(useWide, wq0, q0));
    }

    // port: DoFilter2_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(Vector128<short> p1, Vector128<short> p0, Vector128<short> q0, Vector128<short> q1,
        out Vector128<short> newP0, out Vector128<short> newQ0)
    {
        Vector128<short> a = Diff3(p0, q0) + Clamp(p1 - q1, -128, 127);
        Vector128<short> a1 = Clamp((a + Vector128.Create((short)4)) >> 3, -16, 15);
        Vector128<short> a2 = Clamp((a + Vector128.Create((short)3)) >> 3, -16, 15);
        newP0 = Clip255(p0 + a2);
        newQ0 = Clip255(q0 - a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Diff3(Vector128<short> p0, Vector128<short> q0) => (q0 - p0) * 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clamp(Vector128<short> v, short min, short max) =>
        Vector128.Min(Vector128.Max(v, Vector128.Create(min)), Vector128.Create(max));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Clip255(Vector128<short> v) => Clamp(v, 0, 255);
}
