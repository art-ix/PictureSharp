using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using RasterSharp.Internal;

namespace RasterSharp.Formats.WebP.Lossless;

internal static partial class LosslessTransforms
{
    // port: src/dsp/lossless_sse2.c PredictorAdd10_SSE2
    private static void PredictorAdd10Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i += 4)
        {
            Vector128<byte> topLeft = Load4(ref row, i - width - 1);
            Vector128<byte> average = Simd.Average2(Load4(ref row, i - width), Load4(ref row, i - width + 1));
            left = Predictor10Block(ref row, i, left, Load4(ref row, i), topLeft, average);
        }
    }

    // port: src/dsp/lossless_avx2.c PredictorAdd10_AVX2
    private static void PredictorAdd10Avx2(ref uint row, int start, int width, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i += 8)
        {
            Vector256<byte> source = Load8(ref row, i);
            Vector256<byte> topLeft = Load8(ref row, i - width - 1);
            Vector256<byte> average = Simd.Average2(Load8(ref row, i - width), Load8(ref row, i - width + 1));
            // Preparation covers eight pixels; reconstruction carries left through the two 128-bit lanes.
            left = Predictor10Block(ref row, i, left, source.GetLower(), topLeft.GetLower(), average.GetLower());
            left = Predictor10Block(ref row, i + 4, left, source.GetUpper(), topLeft.GetUpper(), average.GetUpper());
        }
    }

    // port: src/dsp/lossless_sse2.c DO_PRED10, DO_PRED10_SHIFT
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor10Block(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> topLeft, Vector128<byte> average)
    {
        left = Predictor10Pixel(ref row, pos, left, source, topLeft, average);
        left = Predictor10Pixel(ref row, pos + 1, left, ShiftPixel(source), ShiftPixel(topLeft), ShiftPixel(average));
        left = Predictor10Pixel(ref row, pos + 2, left, Sse2.ShiftRightLogical128BitLane(source, 8),
            Sse2.ShiftRightLogical128BitLane(topLeft, 8), Sse2.ShiftRightLogical128BitLane(average, 8));
        return Predictor10Pixel(ref row, pos + 3, left, Sse2.ShiftRightLogical128BitLane(source, 12),
            Sse2.ShiftRightLogical128BitLane(topLeft, 12), Sse2.ShiftRightLogical128BitLane(average, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor10Pixel(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> topLeft, Vector128<byte> average)
    {
        Vector128<byte> result = Sse2.Add(source, Simd.Average2(Simd.Average2(left, topLeft), average));
        Unsafe.Add(ref row, pos) = result.AsUInt32().ToScalar();
        return result;
    }

    // port: src/dsp/lossless_sse2.c PredictorAdd11_SSE2
    private static void PredictorAdd11Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i += 4)
        {
            Vector128<byte> top = Load4(ref row, i - width);
            Vector128<byte> topLeft = Load4(ref row, i - width - 1);
            Vector128<int> distances = TopDistances(top, topLeft);
            left = Predictor11Block(ref row, i, left, Load4(ref row, i), top, topLeft, distances);
        }
    }

    // port: src/dsp/lossless_avx2.c PredictorAdd11_AVX2
    private static void PredictorAdd11Avx2(ref uint row, int start, int width, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i += 8)
        {
            Vector256<byte> source = Load8(ref row, i);
            Vector256<byte> top = Load8(ref row, i - width);
            Vector256<byte> topLeft = Load8(ref row, i - width - 1);
            Vector256<long> low = Avx2.SumAbsoluteDifferences(
                Avx2.UnpackLow(top.AsUInt32(), top.AsUInt32()).AsByte(),
                Avx2.UnpackLow(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
            Vector256<long> high = Avx2.SumAbsoluteDifferences(
                Avx2.UnpackHigh(top.AsUInt32(), top.AsUInt32()).AsByte(),
                Avx2.UnpackHigh(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
            Vector256<int> distances = Avx2.PackSignedSaturate(low.AsInt32(), high.AsInt32()).AsInt32();
            left = Predictor11Block(ref row, i, left, source.GetLower(), top.GetLower(), topLeft.GetLower(), distances.GetLower());
            left = Predictor11Block(ref row, i + 4, left, source.GetUpper(), top.GetUpper(), topLeft.GetUpper(), distances.GetUpper());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> TopDistances(Vector128<byte> top, Vector128<byte> topLeft)
    {
        Vector128<long> low = Sse2.SumAbsoluteDifferences(
            Sse2.UnpackLow(top.AsUInt32(), top.AsUInt32()).AsByte(),
            Sse2.UnpackLow(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
        Vector128<long> high = Sse2.SumAbsoluteDifferences(
            Sse2.UnpackHigh(top.AsUInt32(), top.AsUInt32()).AsByte(),
            Sse2.UnpackHigh(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
        return Sse2.PackSignedSaturate(low.AsInt32(), high.AsInt32()).AsInt32();
    }

    // port: src/dsp/lossless_sse2.c DO_PRED11, DO_PRED11_SHIFT
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor11Block(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> top, Vector128<byte> topLeft, Vector128<int> distances)
    {
        left = Predictor11Pixel(ref row, pos, left, source, top, topLeft, distances);
        left = Predictor11Pixel(ref row, pos + 1, left, ShiftPixel(source), ShiftPixel(top), ShiftPixel(topLeft),
            Sse2.ShiftRightLogical128BitLane(distances, 4));
        left = Predictor11Pixel(ref row, pos + 2, left, Sse2.ShiftRightLogical128BitLane(source, 8),
            Sse2.ShiftRightLogical128BitLane(top, 8), Sse2.ShiftRightLogical128BitLane(topLeft, 8),
            Sse2.ShiftRightLogical128BitLane(distances, 8));
        return Predictor11Pixel(ref row, pos + 3, left, Sse2.ShiftRightLogical128BitLane(source, 12),
            Sse2.ShiftRightLogical128BitLane(top, 12), Sse2.ShiftRightLogical128BitLane(topLeft, 12),
            Sse2.ShiftRightLogical128BitLane(distances, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor11Pixel(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> top, Vector128<byte> topLeft, Vector128<int> distances)
    {
        Vector128<int> distance = Sse2.SumAbsoluteDifferences(
            Sse2.UnpackLow(left.AsUInt32(), top.AsUInt32()).AsByte(),
            Sse2.UnpackLow(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt32();
        Vector128<byte> mask = Sse2.CompareGreaterThan(distance, distances).AsByte();
        Vector128<byte> prediction = Sse2.Or(Sse2.And(mask, left), Sse2.AndNot(mask, top));
        Vector128<byte> result = Sse2.Add(source, prediction);
        Unsafe.Add(ref row, pos) = result.AsUInt32().ToScalar();
        return result;
    }

    // port: src/dsp/lossless_sse2.c PredictorAdd12_SSE2
    private static void PredictorAdd12Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<short> left = Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
        for (int i = start; i < start + count; i += 4)
        {
            Vector128<byte> top = Load4(ref row, i - width);
            Vector128<byte> topLeft = Load4(ref row, i - width - 1);
            Vector128<short> low = Sse2.Subtract(Sse2.UnpackLow(top, Vector128<byte>.Zero).AsInt16(),
                Sse2.UnpackLow(topLeft, Vector128<byte>.Zero).AsInt16());
            Vector128<short> high = Sse2.Subtract(Sse2.UnpackHigh(top, Vector128<byte>.Zero).AsInt16(),
                Sse2.UnpackHigh(topLeft, Vector128<byte>.Zero).AsInt16());
            left = Predictor12Block(ref row, i, left, Load4(ref row, i), low, high);
        }
    }

    // port: src/dsp/lossless_avx2.c PredictorAdd12_AVX2
    private static void PredictorAdd12Avx2(ref uint row, int start, int width, int count)
    {
        Vector128<short> left = Sse2.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
        for (int i = start; i < start + count; i += 8)
        {
            Vector256<byte> source = Load8(ref row, i);
            Vector256<byte> top = Load8(ref row, i - width);
            Vector256<byte> topLeft = Load8(ref row, i - width - 1);
            Vector256<short> low = Avx2.Subtract(Avx2.UnpackLow(top, Vector256<byte>.Zero).AsInt16(),
                Avx2.UnpackLow(topLeft, Vector256<byte>.Zero).AsInt16());
            Vector256<short> high = Avx2.Subtract(Avx2.UnpackHigh(top, Vector256<byte>.Zero).AsInt16(),
                Avx2.UnpackHigh(topLeft, Vector256<byte>.Zero).AsInt16());
            left = Predictor12Block(ref row, i, left, source.GetLower(), low.GetLower(), high.GetLower());
            left = Predictor12Block(ref row, i + 4, left, source.GetUpper(), low.GetUpper(), high.GetUpper());
        }
    }

    // port: src/dsp/lossless_sse2.c DO_PRED12, DO_PRED12_SHIFT
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Predictor12Block(ref uint row, int pos, Vector128<short> left,
        Vector128<byte> source, Vector128<short> low, Vector128<short> high)
    {
        left = Predictor12Pixel(ref row, pos, left, source, low);
        left = Predictor12Pixel(ref row, pos + 1, left, ShiftPixel(source), Sse2.ShiftRightLogical128BitLane(low, 8));
        left = Predictor12Pixel(ref row, pos + 2, left, Sse2.ShiftRightLogical128BitLane(source, 8), high);
        return Predictor12Pixel(ref row, pos + 3, left, Sse2.ShiftRightLogical128BitLane(source, 12),
            Sse2.ShiftRightLogical128BitLane(high, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Predictor12Pixel(ref uint row, int pos, Vector128<short> left,
        Vector128<byte> source, Vector128<short> difference)
    {
        Vector128<short> sum = Sse2.Add(left, difference);
        Vector128<byte> prediction = Sse2.PackUnsignedSaturate(sum, sum);
        Vector128<byte> result = Sse2.Add(source, prediction);
        Unsafe.Add(ref row, pos) = result.AsUInt32().ToScalar();
        return Sse2.UnpackLow(result, Vector128<byte>.Zero).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ShiftPixel(Vector128<byte> value) => Sse2.ShiftRightLogical128BitLane(value, 4);
}
