using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using PictureSharp.Infrastructure.Codecs.WebP;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossless;

internal static partial class LosslessTransforms
{
    /// <summary>
    /// Vector prefix of one predictor run. Modes 3 and 9 read one pixel past the run on the row above,
    /// which stays inside the image buffer (the next tile, or column 0 of the current row at the right edge).
    /// </summary>
    private static int PredictorRunSimd(int mode, uint[] pixels, int start, int end, int width)
    {
        if (!Simd.IsSupported128)
        {
            return 0;
        }

        int count = end - start;
        if (mode is 14 or 15)
        {
            mode = 0;
        }

        if (mode == 1)
        {
            ScanlineSimd.AddLeftPacked(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(pixels), start), count);
            return count;
        }

        if (mode is 5 or 6 or 7 or 13)
        {
            ref uint pixelsStart = ref MemoryMarshal.GetArrayDataReference(pixels);
            switch (mode)
            {
                case 5:
                    PredictorAdd5Sse2(ref pixelsStart, start, width, count);
                    break;
                case 6:
                case 7:
                    PredictorAddAverageSse2(ref pixelsStart, start, width + (mode == 6 ? 1 : 0), count);
                    break;
                default:
                    PredictorAdd13Sse2(ref pixelsStart, start, width, count);
                    break;
            }

            return count;
        }

        if (mode is not (0 or 2 or 3 or 4 or 8 or 9 or 10 or 11 or 12))
        {
            return 0;
        }

        ref uint row = ref MemoryMarshal.GetArrayDataReference(pixels);
        ref uint dst = ref Unsafe.Add(ref row, start);
        int done = 0;
        if (Avx2.IsSupported)
        {
            int n = count & ~7;
            if (n >= 8)
            {
                AddRun8(mode, ref dst, ref row, start, width, n);
                done = n;
            }
        }

        int tail = (count - done) & ~3;
        if (tail >= 4)
        {
            AddRun4(mode, ref Unsafe.Add(ref dst, done), ref row, start + done, width, tail);
            done += tail;
        }

        return done;
    }

    private static void AddRun8(int mode, ref uint dst, ref uint row, int start, int width, int count)
    {
        switch (mode)
        {
            case 10:
                PredictorAdd10Avx2(ref row, start, width, count);
                break;
            case 11:
                PredictorAdd11Avx2(ref row, start, width, count);
                break;
            case 12:
                PredictorAdd12Avx2(ref row, start, width, count);
                break;
            case 0:
                Vector256<byte> black = Vector256.Create(WebPConstants.ArgbBlack).AsByte();
                for (int i = 0; i < count; i += 8)
                {
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), black));
                }

                break;
            case 2:
                for (int i = 0; i < count; i += 8)
                {
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), Load8(ref row, start - width + i)));
                }

                break;
            case 3:
                for (int i = 0; i < count; i += 8)
                {
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), Load8(ref row, start - width + 1 + i)));
                }

                break;
            case 4:
                for (int i = 0; i < count; i += 8)
                {
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), Load8(ref row, start - width - 1 + i)));
                }

                break;
            case 8:
                for (int i = 0; i < count; i += 8)
                {
                    Vector256<byte> avg = Simd.Average2(Load8(ref row, start - width + i), Load8(ref row, start - width - 1 + i));
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), avg));
                }

                break;
            default:
                for (int i = 0; i < count; i += 8)
                {
                    Vector256<byte> avg = Simd.Average2(Load8(ref row, start - width + i), Load8(ref row, start - width + 1 + i));
                    Store8(ref dst, i, Avx2.Add(Load8(ref dst, i), avg));
                }

                break;
        }
    }

    private static void AddRun4(int mode, ref uint dst, ref uint row, int start, int width, int count)
    {
        switch (mode)
        {
            case 10:
                PredictorAdd10Sse2(ref row, start, width, count);
                break;
            case 11:
                PredictorAdd11Sse2(ref row, start, width, count);
                break;
            case 12:
                PredictorAdd12Sse2(ref row, start, width, count);
                break;
            case 0:
                Vector128<byte> black = Vector128.Create(WebPConstants.ArgbBlack).AsByte();
                for (int i = 0; i < count; i += 4)
                {
                    Store4(ref dst, i, (Load4(ref dst, i) + black));
                }

                break;
            case 2:
                for (int i = 0; i < count; i += 4)
                {
                    Store4(ref dst, i, (Load4(ref dst, i) + Load4(ref row, start - width + i)));
                }

                break;
            case 3:
                for (int i = 0; i < count; i += 4)
                {
                    Store4(ref dst, i, (Load4(ref dst, i) + Load4(ref row, start - width + 1 + i)));
                }

                break;
            case 4:
                for (int i = 0; i < count; i += 4)
                {
                    Store4(ref dst, i, (Load4(ref dst, i) + Load4(ref row, start - width - 1 + i)));
                }

                break;
            case 8:
                for (int i = 0; i < count; i += 4)
                {
                    Vector128<byte> avg = Simd.Average2(Load4(ref row, start - width + i), Load4(ref row, start - width - 1 + i));
                    Store4(ref dst, i, (Load4(ref dst, i) + avg));
                }

                break;
            default:
                for (int i = 0; i < count; i += 4)
                {
                    Vector128<byte> avg = Simd.Average2(Load4(ref row, start - width + i), Load4(ref row, start - width + 1 + i));
                    Store4(ref dst, i, (Load4(ref dst, i) + avg));
                }

                break;
        }
    }

    // port: src/dsp/lossless_sse2.c Predictor5_SSE2, GENERATE_PREDICTOR_ADD
    private static void PredictorAdd5Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i++)
        {
            Vector128<byte> top = Vector128.CreateScalar(Unsafe.Add(ref row, i - width)).AsByte();
            Vector128<byte> topRight = Vector128.CreateScalar(Unsafe.Add(ref row, i - width + 1)).AsByte();
            Vector128<byte> source = Vector128.CreateScalar(Unsafe.Add(ref row, i)).AsByte();
            left = source + Simd.Average2(Simd.Average2(left, topRight), top);
            Unsafe.Add(ref row, i) = left.AsUInt32().ToScalar();
        }
    }

    // port: src/dsp/lossless_sse2.c Predictor6_SSE2, Predictor7_SSE2, GENERATE_PREDICTOR_ADD
    private static void PredictorAddAverageSse2(ref uint row, int start, int topOffset, int count)
    {
        Vector128<byte> left = Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte();
        for (int i = start; i < start + count; i++)
        {
            Vector128<byte> top = Vector128.CreateScalar(Unsafe.Add(ref row, i - topOffset)).AsByte();
            Vector128<byte> source = Vector128.CreateScalar(Unsafe.Add(ref row, i)).AsByte();
            left = source + Simd.Average2(left, top);
            Unsafe.Add(ref row, i) = left.AsUInt32().ToScalar();
        }
    }

    // port: src/dsp/lossless_sse2.c ClampedAddSubtractHalf_SSE2, Predictor13_SSE2, GENERATE_PREDICTOR_ADD
    private static void PredictorAdd13Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<short> left = Simd.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
        for (int i = start; i < start + count; i++)
        {
            Vector128<short> top = Simd.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, i - width)).AsByte(), Vector128<byte>.Zero).AsInt16();
            Vector128<short> topLeft = Simd.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, i - width - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
            Vector128<short> average = (left + top) >>> 1;
            Vector128<short> difference = average - topLeft;
            // C division truncates toward zero: correct negative odd differences before shifting.
            difference -= Vector128.GreaterThan(topLeft, average);
            Vector128<short> prediction = average + (difference >> 1);
            Vector128<byte> source = Vector128.CreateScalar(Unsafe.Add(ref row, i)).AsByte();
            Vector128<byte> result = source + Simd.PackUnsignedSaturate(prediction, prediction);
            Unsafe.Add(ref row, i) = result.AsUInt32().ToScalar();
            left = Simd.UnpackLow(result, Vector128<byte>.Zero).AsInt16();
        }
    }

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
        left = Predictor10Pixel(ref row, pos + 2, left, Simd.ShiftRightBytes(source, 8),
            Simd.ShiftRightBytes(topLeft, 8), Simd.ShiftRightBytes(average, 8));
        return Predictor10Pixel(ref row, pos + 3, left, Simd.ShiftRightBytes(source, 12),
            Simd.ShiftRightBytes(topLeft, 12), Simd.ShiftRightBytes(average, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor10Pixel(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> topLeft, Vector128<byte> average)
    {
        Vector128<byte> result = source + Simd.Average2(Simd.Average2(left, topLeft), average);
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
        Vector128<long> low = Simd.SumAbsoluteDifferences(Simd.UnpackLow(top.AsUInt32(), top.AsUInt32()).AsByte(), Simd.UnpackLow(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
        Vector128<long> high = Simd.SumAbsoluteDifferences(Simd.UnpackHigh(top.AsUInt32(), top.AsUInt32()).AsByte(), Simd.UnpackHigh(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt64();
        return Simd.PackSignedSaturate(low.AsInt32(), high.AsInt32()).AsInt32();
    }

    // port: src/dsp/lossless_sse2.c DO_PRED11, DO_PRED11_SHIFT
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor11Block(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> top, Vector128<byte> topLeft, Vector128<int> distances)
    {
        left = Predictor11Pixel(ref row, pos, left, source, top, topLeft, distances);
        left = Predictor11Pixel(ref row, pos + 1, left, ShiftPixel(source), ShiftPixel(top), ShiftPixel(topLeft),
            Simd.ShiftRightBytes(distances, 4));
        left = Predictor11Pixel(ref row, pos + 2, left, Simd.ShiftRightBytes(source, 8),
            Simd.ShiftRightBytes(top, 8), Simd.ShiftRightBytes(topLeft, 8),
            Simd.ShiftRightBytes(distances, 8));
        return Predictor11Pixel(ref row, pos + 3, left, Simd.ShiftRightBytes(source, 12),
            Simd.ShiftRightBytes(top, 12), Simd.ShiftRightBytes(topLeft, 12),
            Simd.ShiftRightBytes(distances, 12));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Predictor11Pixel(ref uint row, int pos, Vector128<byte> left,
        Vector128<byte> source, Vector128<byte> top, Vector128<byte> topLeft, Vector128<int> distances)
    {
        Vector128<int> distance = Simd.SumAbsoluteDifferences(Simd.UnpackLow(left.AsUInt32(), top.AsUInt32()).AsByte(), Simd.UnpackLow(topLeft.AsUInt32(), top.AsUInt32()).AsByte()).AsInt32();
        Vector128<byte> mask = Vector128.GreaterThan(distance, distances).AsByte();
        Vector128<byte> prediction = (mask & left) | Vector128.AndNot(top, mask);
        Vector128<byte> result = source + prediction;
        Unsafe.Add(ref row, pos) = result.AsUInt32().ToScalar();
        return result;
    }

    // port: src/dsp/lossless_sse2.c PredictorAdd12_SSE2
    private static void PredictorAdd12Sse2(ref uint row, int start, int width, int count)
    {
        Vector128<short> left = Simd.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
        for (int i = start; i < start + count; i += 4)
        {
            Vector128<byte> top = Load4(ref row, i - width);
            Vector128<byte> topLeft = Load4(ref row, i - width - 1);
            Vector128<short> low = Simd.UnpackLow(top, Vector128<byte>.Zero).AsInt16() - Simd.UnpackLow(topLeft, Vector128<byte>.Zero).AsInt16();
            Vector128<short> high = Simd.UnpackHigh(top, Vector128<byte>.Zero).AsInt16() - Simd.UnpackHigh(topLeft, Vector128<byte>.Zero).AsInt16();
            left = Predictor12Block(ref row, i, left, Load4(ref row, i), low, high);
        }
    }

    // port: src/dsp/lossless_avx2.c PredictorAdd12_AVX2
    private static void PredictorAdd12Avx2(ref uint row, int start, int width, int count)
    {
        Vector128<short> left = Simd.UnpackLow(Vector128.CreateScalar(Unsafe.Add(ref row, start - 1)).AsByte(), Vector128<byte>.Zero).AsInt16();
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
        left = Predictor12Pixel(ref row, pos + 1, left, ShiftPixel(source), Simd.ShiftRightBytes(low, 8));
        left = Predictor12Pixel(ref row, pos + 2, left, Simd.ShiftRightBytes(source, 8), high);
        return Predictor12Pixel(ref row, pos + 3, left, Simd.ShiftRightBytes(source, 12),
            Simd.ShiftRightBytes(high, 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<short> Predictor12Pixel(ref uint row, int pos, Vector128<short> left,
        Vector128<byte> source, Vector128<short> difference)
    {
        Vector128<short> sum = left + difference;
        Vector128<byte> prediction = Simd.PackUnsignedSaturate(sum, sum);
        Vector128<byte> result = source + prediction;
        Unsafe.Add(ref row, pos) = result.AsUInt32().ToScalar();
        return Simd.UnpackLow(result, Vector128<byte>.Zero).AsInt16();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> ShiftPixel(Vector128<byte> value) => Simd.ShiftRightBytes(value, 4);
}
