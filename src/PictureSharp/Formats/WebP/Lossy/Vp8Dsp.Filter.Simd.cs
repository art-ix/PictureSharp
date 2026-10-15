using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Internal;

namespace PictureSharp.Formats.WebP.Lossy;

// SIMD in-loop filters: the 16 positions along an edge (one luma edge, or 8 U and 8 V positions) are filtered
// at once in 16-bit lanes, using the clamps of the C code, so the result equals the scalar filters for every
// input. Positions along an edge are independent; edges are still filtered in the scalar order. ARM64 uses
// Vp8Dsp.Filter.Arm.cs.
internal static partial class Vp8Dsp
{
    private enum EdgeFilter { Simple, Inner, MacroblockEdge }

    public static void SimpleVFilter16(byte[] p, int o, int stride, int thresh)
    {
        if (FilterSimdSupported) FilterAcrossRows(p, o, p, o + 8, stride, EdgeFilter.Simple, thresh, 0, 0);
        else SimpleVFilter16Scalar(p, o, stride, thresh);
    }

    public static void SimpleHFilter16(byte[] p, int o, int stride, int thresh)
    {
        if (FilterSimdSupported) FilterAcrossColumns(p, o, p, o + (8 * stride), stride, EdgeFilter.Simple, thresh, 0, 0);
        else SimpleHFilter16Scalar(p, o, stride, thresh);
    }

    public static void SimpleVFilter16i(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            SimpleVFilter16(p, o, stride, thresh);
        }
    }

    public static void SimpleHFilter16i(byte[] p, int o, int stride, int thresh)
    {
        for (int k = 3; k > 0; --k)
        {
            o += 4;
            SimpleHFilter16(p, o, stride, thresh);
        }
    }

    public static void VFilter16(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossRows(p, o, p, o + 8, stride, EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        else VFilter16Scalar(p, o, stride, thresh, ithresh, hevThresh);
    }

    public static void HFilter16(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossColumns(p, o, p, o + (8 * stride), stride, EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        else HFilter16Scalar(p, o, stride, thresh, ithresh, hevThresh);
    }

    public static void VFilter16i(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (!FilterSimdSupported)
        {
            VFilter16iScalar(p, o, stride, thresh, ithresh, hevThresh);
            return;
        }

        for (int k = 3; k > 0; --k)
        {
            o += 4 * stride;
            FilterAcrossRows(p, o, p, o + 8, stride, EdgeFilter.Inner, thresh, ithresh, hevThresh);
        }
    }

    public static void HFilter16i(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (!FilterSimdSupported)
        {
            HFilter16iScalar(p, o, stride, thresh, ithresh, hevThresh);
            return;
        }

        for (int k = 3; k > 0; --k)
        {
            o += 4;
            FilterAcrossColumns(p, o, p, o + (8 * stride), stride, EdgeFilter.Inner, thresh, ithresh, hevThresh);
        }
    }

    public static void VFilter8(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossRows(u, uo, v, vo, stride, EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        else VFilter8Scalar(u, uo, v, vo, stride, thresh, ithresh, hevThresh);
    }

    public static void HFilter8(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossColumns(u, uo, v, vo, stride, EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        else HFilter8Scalar(u, uo, v, vo, stride, thresh, ithresh, hevThresh);
    }

    public static void VFilter8i(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossRows(u, uo + (4 * stride), v, vo + (4 * stride), stride, EdgeFilter.Inner, thresh, ithresh, hevThresh);
        else VFilter8iScalar(u, uo, v, vo, stride, thresh, ithresh, hevThresh);
    }

    public static void HFilter8i(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh)
    {
        if (FilterSimdSupported) FilterAcrossColumns(u, uo + 4, v, vo + 4, stride, EdgeFilter.Inner, thresh, ithresh, hevThresh);
        else HFilter8iScalar(u, uo, v, vo, stride, thresh, ithresh, hevThresh);
    }

    private static bool FilterSimdSupported
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Avx2.IsSupported || AdvSimd.Arm64.IsSupported;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Load8(byte[] p, int o) => BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(o, 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store8(byte[] p, int o, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(p.AsSpan(o, 8), value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Widen(Vector128<byte> v) => Avx2.ConvertToVector256Int16(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Narrow(Vector256<short> v) => Simd.PackUnsignedSaturate(v.GetLower(), v.GetUpper());

    // Horizontal edge at row offset 'ao' (lanes 0-7, 8 bytes) and 'bo' (lanes 8-15): rows are loaded directly.
    private static void FilterAcrossRows(byte[] a, int ao, byte[] b, int bo, int stride, EdgeFilter filter, int thresh, int ithresh, int hevThresh)
    {
        if (!Avx2.IsSupported)
        {
            FilterAcrossRowsArm(a, ao, b, bo, stride, filter, thresh, ithresh, hevThresh);
            return;
        }

        Vector256<short> Row(int k) => Widen(Vector128.Create(Load8(a, ao + (k * stride)), Load8(b, bo + (k * stride))).AsByte());
        void Store(int k, Vector256<short> value)
        {
            Vector128<ulong> bytes = Narrow(value).AsUInt64();
            Store8(a, ao + (k * stride), bytes.GetElement(0));
            Store8(b, bo + (k * stride), bytes.GetElement(1));
        }

        if (filter == EdgeFilter.Simple)
        {
            Vector256<short> sp1 = Row(-2), sp0 = Row(-1), sq0 = Row(0), sq1 = Row(1);
            SimpleFilter(sp1, ref sp0, ref sq0, sq1, thresh);
            Store(-1, sp0);
            Store(0, sq0);
            return;
        }

        Vector256<short> p3 = Row(-4), p2 = Row(-3), p1 = Row(-2), p0 = Row(-1);
        Vector256<short> q0 = Row(0), q1 = Row(1), q2 = Row(2), q3 = Row(3);
        ComplexFilter(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, filter == EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
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

    // Vertical edge at column offset 'ao' (8 rows from there are lanes 0-7) and 'bo' (lanes 8-15): the 8 columns
    // around the edge are transposed into vectors, filtered, and transposed back.
    private static void FilterAcrossColumns(byte[] a, int ao, byte[] b, int bo, int stride, EdgeFilter filter, int thresh, int ithresh, int hevThresh)
    {
        if (!Avx2.IsSupported)
        {
            FilterAcrossColumnsArm(a, ao, b, bo, stride, filter, thresh, ithresh, hevThresh);
            return;
        }

        Load16x8(a, ao - 4, b, bo - 4, stride, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
            out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7);
        Vector256<short> p3 = Widen(c0), p2 = Widen(c1), p1 = Widen(c2), p0 = Widen(c3);
        Vector256<short> q0 = Widen(c4), q1 = Widen(c5), q2 = Widen(c6), q3 = Widen(c7);
        if (filter == EdgeFilter.Simple) SimpleFilter(p1, ref p0, ref q0, q1, thresh);
        else ComplexFilter(p3, ref p2, ref p1, ref p0, ref q0, ref q1, ref q2, q3, filter == EdgeFilter.MacroblockEdge, thresh, ithresh, hevThresh);
        // Unchanged columns are written back with their own values.
        Store16x8(a, ao - 4, b, bo - 4, stride, c0, Narrow(p2), Narrow(p1), Narrow(p0), Narrow(q0), Narrow(q1), Narrow(q2), c7);
    }

    // port: NeedsFilter_C + DoFilter2_C on every lane
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SimpleFilter(Vector256<short> p1, ref Vector256<short> p0, ref Vector256<short> q0, Vector256<short> q1, int thresh)
    {
        Vector256<short> needs = Vector256.LessThanOrEqual((Vector256.Abs(p0 - q0) << 2) + Vector256.Abs(p1 - q1), Vector256.Create((short)((2 * thresh) + 1)));
        DoFilter2(p1, p0, q0, q1, out Vector256<short> np0, out Vector256<short> nq0);
        p0 = Vector256.ConditionalSelect(needs, np0, p0);
        q0 = Vector256.ConditionalSelect(needs, nq0, q0);
    }

    // port: FilterLoop26_C (macroblock edges) and FilterLoop24_C (inner edges) on every lane
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ComplexFilter(Vector256<short> p3, ref Vector256<short> p2, ref Vector256<short> p1, ref Vector256<short> p0,
        ref Vector256<short> q0, ref Vector256<short> q1, ref Vector256<short> q2, Vector256<short> q3,
        bool macroblockEdge, int thresh, int ithresh, int hevThresh)
    {
        // NeedsFilter2_C
        Vector256<short> limit = Vector256.Create((short)ithresh);
        Vector256<short> edge = (Vector256.Abs(p0 - q0) << 2) + Vector256.Abs(p1 - q1);
        Vector256<short> innerMax = Vector256.Max(Vector256.Max(Vector256.Abs(p3 - p2), Vector256.Abs(p2 - p1)), Vector256.Abs(q3 - q2));
        Vector256<short> p1p0 = Vector256.Abs(p1 - p0), q1q0 = Vector256.Abs(q1 - q0);
        innerMax = Vector256.Max(Vector256.Max(innerMax, Vector256.Abs(q2 - q1)), Vector256.Max(p1p0, q1q0));
        Vector256<short> needs = Vector256.LessThanOrEqual(edge, Vector256.Create((short)((2 * thresh) + 1))) & Vector256.LessThanOrEqual(innerMax, limit);
        if (needs == Vector256<short>.Zero) return;

        // Hev
        Vector256<short> hev = Vector256.GreaterThan(Vector256.Max(p1p0, q1q0), Vector256.Create((short)hevThresh));
        Vector256<short> useFilter2 = needs & hev, useWide = Vector256.AndNot(needs, hev);
        DoFilter2(p1, p0, q0, q1, out Vector256<short> f2p0, out Vector256<short> f2q0);
        Vector256<short> wp1, wp0, wq0, wq1;
        if (macroblockEdge)
        {
            // DoFilter6_C
            Vector256<short> a = Clamp(Diff3(p0, q0) + Clamp(p1 - q1, -128, 127), -128, 127);
            Vector256<short> rounder = Vector256.Create((short)63);
            Vector256<short> a1 = ((a * 27) + rounder) >> 7, a2 = ((a * 18) + rounder) >> 7, a3 = ((a * 9) + rounder) >> 7;
            p2 = Vector256.ConditionalSelect(useWide, Clip255(p2 + a3), p2);
            q2 = Vector256.ConditionalSelect(useWide, Clip255(q2 - a3), q2);
            wp1 = Clip255(p1 + a2);
            wp0 = Clip255(p0 + a1);
            wq0 = Clip255(q0 - a1);
            wq1 = Clip255(q1 - a2);
        }
        else
        {
            // DoFilter4_C
            Vector256<short> a = Diff3(p0, q0);
            Vector256<short> a1 = Clamp((a + Vector256.Create((short)4)) >> 3, -16, 15);
            Vector256<short> a2 = Clamp((a + Vector256.Create((short)3)) >> 3, -16, 15);
            Vector256<short> a3 = (a1 + Vector256<short>.One) >> 1;
            wp1 = Clip255(p1 + a3);
            wp0 = Clip255(p0 + a2);
            wq0 = Clip255(q0 - a1);
            wq1 = Clip255(q1 - a3);
        }

        p1 = Vector256.ConditionalSelect(useWide, wp1, p1);
        q1 = Vector256.ConditionalSelect(useWide, wq1, q1);
        p0 = Vector256.ConditionalSelect(useFilter2, f2p0, Vector256.ConditionalSelect(useWide, wp0, p0));
        q0 = Vector256.ConditionalSelect(useFilter2, f2q0, Vector256.ConditionalSelect(useWide, wq0, q0));
    }

    // port: DoFilter2_C
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DoFilter2(Vector256<short> p1, Vector256<short> p0, Vector256<short> q0, Vector256<short> q1,
        out Vector256<short> newP0, out Vector256<short> newQ0)
    {
        Vector256<short> a = Diff3(p0, q0) + Clamp(p1 - q1, -128, 127);
        Vector256<short> a1 = Clamp((a + Vector256.Create((short)4)) >> 3, -16, 15);
        Vector256<short> a2 = Clamp((a + Vector256.Create((short)3)) >> 3, -16, 15);
        newP0 = Clip255(p0 + a2);
        newQ0 = Clip255(q0 - a1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Diff3(Vector256<short> p0, Vector256<short> q0) => (q0 - p0) * 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Clamp(Vector256<short> v, short min, short max) =>
        Vector256.Min(Vector256.Max(v, Vector256.Create(min)), Vector256.Create(max));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<short> Clip255(Vector256<short> v) => Clamp(v, 0, 255);

    // Transposes 8 bytes from each of 8 rows of 'a' and 8 rows of 'b' into 8 vectors of 16 bytes (one per column).
    private static void Load16x8(byte[] a, int ao, byte[] b, int bo, int stride,
        out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3,
        out Vector128<byte> c4, out Vector128<byte> c5, out Vector128<byte> c6, out Vector128<byte> c7)
    {
        Vector128<byte> Pair(byte[] p, int o) => Simd.UnpackLow(Vector128.CreateScalar(Load8(p, o)).AsByte(), Vector128.CreateScalar(Load8(p, o + stride)).AsByte());

        // Byte pairs of rows (0,1), (2,3), ...
        Vector128<ushort> r01 = Pair(a, ao).AsUInt16(), r23 = Pair(a, ao + (2 * stride)).AsUInt16();
        Vector128<ushort> r45 = Pair(a, ao + (4 * stride)).AsUInt16(), r67 = Pair(a, ao + (6 * stride)).AsUInt16();
        Vector128<ushort> r89 = Pair(b, bo).AsUInt16(), rAB = Pair(b, bo + (2 * stride)).AsUInt16();
        Vector128<ushort> rCD = Pair(b, bo + (4 * stride)).AsUInt16(), rEF = Pair(b, bo + (6 * stride)).AsUInt16();
        // Columns 0-3 and 4-7 of row quads.
        Vector128<uint> q0Low = Simd.UnpackLow(r01, r23).AsUInt32(), q0High = Simd.UnpackHigh(r01, r23).AsUInt32();
        Vector128<uint> q1Low = Simd.UnpackLow(r45, r67).AsUInt32(), q1High = Simd.UnpackHigh(r45, r67).AsUInt32();
        Vector128<uint> q2Low = Simd.UnpackLow(r89, rAB).AsUInt32(), q2High = Simd.UnpackHigh(r89, rAB).AsUInt32();
        Vector128<uint> q3Low = Simd.UnpackLow(rCD, rEF).AsUInt32(), q3High = Simd.UnpackHigh(rCD, rEF).AsUInt32();
        // Pairs of columns over 8 rows.
        Vector128<ulong> c01Top = Simd.UnpackLow(q0Low, q1Low).AsUInt64(), c23Top = Simd.UnpackHigh(q0Low, q1Low).AsUInt64();
        Vector128<ulong> c45Top = Simd.UnpackLow(q0High, q1High).AsUInt64(), c67Top = Simd.UnpackHigh(q0High, q1High).AsUInt64();
        Vector128<ulong> c01Bottom = Simd.UnpackLow(q2Low, q3Low).AsUInt64(), c23Bottom = Simd.UnpackHigh(q2Low, q3Low).AsUInt64();
        Vector128<ulong> c45Bottom = Simd.UnpackLow(q2High, q3High).AsUInt64(), c67Bottom = Simd.UnpackHigh(q2High, q3High).AsUInt64();
        c0 = Simd.UnpackLow(c01Top, c01Bottom).AsByte();
        c1 = Simd.UnpackHigh(c01Top, c01Bottom).AsByte();
        c2 = Simd.UnpackLow(c23Top, c23Bottom).AsByte();
        c3 = Simd.UnpackHigh(c23Top, c23Bottom).AsByte();
        c4 = Simd.UnpackLow(c45Top, c45Bottom).AsByte();
        c5 = Simd.UnpackHigh(c45Top, c45Bottom).AsByte();
        c6 = Simd.UnpackLow(c67Top, c67Bottom).AsByte();
        c7 = Simd.UnpackHigh(c67Top, c67Bottom).AsByte();
    }

    // Inverse of Load16x8.
    private static void Store16x8(byte[] a, int ao, byte[] b, int bo, int stride,
        Vector128<byte> c0, Vector128<byte> c1, Vector128<byte> c2, Vector128<byte> c3,
        Vector128<byte> c4, Vector128<byte> c5, Vector128<byte> c6, Vector128<byte> c7)
    {
        // Byte pairs of columns (0,1), ... for rows 0-7 (low) and 8-15 (high).
        Vector128<ushort> c01Low = Simd.UnpackLow(c0, c1).AsUInt16(), c01High = Simd.UnpackHigh(c0, c1).AsUInt16();
        Vector128<ushort> c23Low = Simd.UnpackLow(c2, c3).AsUInt16(), c23High = Simd.UnpackHigh(c2, c3).AsUInt16();
        Vector128<ushort> c45Low = Simd.UnpackLow(c4, c5).AsUInt16(), c45High = Simd.UnpackHigh(c4, c5).AsUInt16();
        Vector128<ushort> c67Low = Simd.UnpackLow(c6, c7).AsUInt16(), c67High = Simd.UnpackHigh(c6, c7).AsUInt16();
        StoreRows(a, ao, stride, c01Low, c23Low, c45Low, c67Low);
        StoreRows(b, bo, stride, c01High, c23High, c45High, c67High);

        static void StoreRows(byte[] p, int o, int stride, Vector128<ushort> c01, Vector128<ushort> c23, Vector128<ushort> c45, Vector128<ushort> c67)
        {
            // Columns 0-3 and 4-7 of rows 0-3 and 4-7.
            Vector128<uint> left03 = Simd.UnpackLow(c01, c23).AsUInt32(), left47 = Simd.UnpackHigh(c01, c23).AsUInt32();
            Vector128<uint> right03 = Simd.UnpackLow(c45, c67).AsUInt32(), right47 = Simd.UnpackHigh(c45, c67).AsUInt32();
            Vector128<ulong> rows01 = Simd.UnpackLow(left03, right03).AsUInt64(), rows23 = Simd.UnpackHigh(left03, right03).AsUInt64();
            Vector128<ulong> rows45 = Simd.UnpackLow(left47, right47).AsUInt64(), rows67 = Simd.UnpackHigh(left47, right47).AsUInt64();
            Store8(p, o, rows01.GetElement(0));
            Store8(p, o + stride, rows01.GetElement(1));
            Store8(p, o + (2 * stride), rows23.GetElement(0));
            Store8(p, o + (3 * stride), rows23.GetElement(1));
            Store8(p, o + (4 * stride), rows45.GetElement(0));
            Store8(p, o + (5 * stride), rows45.GetElement(1));
            Store8(p, o + (6 * stride), rows67.GetElement(0));
            Store8(p, o + (7 * stride), rows67.GetElement(1));
        }
    }
}
