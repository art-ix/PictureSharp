using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Resizer
{
    /// <summary>
    /// The geometry and weights of <see cref="ResampleInner"/> applied in linear-light sRGB: pixels are decoded with
    /// the sRGB transfer function, premultiplied by alpha in float, resampled like Pillow's 32-bit float passes and
    /// encoded back with straight alpha.
    /// </summary>
    private static void ResampleLinear(Rgba32[] src, int srcWidth, int srcHeight, Rgba32[] dst, int width, int height,
        ResamplingFilter filter, float x0, float y0, float x1, float y1, ParallelOptions parallel)
    {
        bool needHorizontal = width != srcWidth || x0 != 0 || x1 != width;
        bool needVertical = height != srcHeight || y0 != 0 || y1 != height;
        (int ksizeH, int[] boundsH, double[] weightsH) = PrecomputeWeights(srcWidth, x0, x1, width, filter);
        (int ksizeV, int[] boundsV, double[] weightsV) = PrecomputeWeights(srcHeight, y0, y1, height, filter);
        float[] kH = ToSingle(weightsH), kV = ToSingle(weightsV);

        // Output rows go in bands: a band converts and resamples horizontally only the source rows its vertical taps
        // cover, so no buffer spans the image. Rows shared with the neighbouring band are computed again by the same
        // operations, so the result does not depend on the banding.
        int bands = (height + LinearBandRows - 1) / LinearBandRows;
        long workPerBand = (long)Math.Min(LinearBandRows, height) * ((width * (ksizeV + 1)) + (srcWidth * (srcHeight / Math.Max(1, height))));
        ForRows(bands, workPerBand, parallel, band =>
        {
            int first = band * LinearBandRows, last = Math.Min(height, first + LinearBandRows);
            // Vertical taps start and end at non-decreasing source rows.
            int sourceStart = needVertical ? boundsV[first * 2] : first;
            int sourceEnd = needVertical ? boundsV[(last - 1) * 2] + boundsV[((last - 1) * 2) + 1] : last;
            int rows = sourceEnd - sourceStart;
            Vector4[] horizontal = ArrayPool<Vector4>.Shared.Rent(rows * width);
            Vector4[]? line = needHorizontal ? ArrayPool<Vector4>.Shared.Rent(srcWidth) : null;
            Vector4[]? vertical = needVertical ? ArrayPool<Vector4>.Shared.Rent(width) : null;
            try
            {
                for (int r = 0; r < rows; r++)
                {
                    ReadOnlySpan<Rgba32> sourceRow = src.AsSpan((sourceStart + r) * srcWidth, srcWidth);
                    Span<Vector4> outRow = horizontal.AsSpan(r * width, width);
                    if (line is null)
                    {
                        ToLinearPremultiplied(sourceRow, outRow);
                        continue;
                    }

                    Span<Vector4> inRow = line.AsSpan(0, srcWidth);
                    ToLinearPremultiplied(sourceRow, inRow);
                    for (int xx = 0; xx < width; xx++)
                    {
                        ReadOnlySpan<float> w = kH.AsSpan(xx * ksizeH, boundsH[(xx * 2) + 1]);
                        ReadOnlySpan<Vector4> px = inRow.Slice(boundsH[xx * 2], w.Length);
                        Vector4 sum = Vector4.Zero;
                        for (int x = 0; x < w.Length; x++)
                        {
                            sum += px[x] * w[x];
                        }

                        outRow[xx] = sum;
                    }
                }

                for (int yy = first; yy < last; yy++)
                {
                    Span<Rgba32> target = dst.AsSpan(yy * width, width);
                    if (vertical is null)
                    {
                        FromLinearPremultiplied(horizontal.AsSpan((yy - sourceStart) * width, width), target);
                        continue;
                    }

                    ReadOnlySpan<float> w = kV.AsSpan(yy * ksizeV, boundsV[(yy * 2) + 1]);
                    int ymin = boundsV[yy * 2] - sourceStart;
                    Span<Vector4> outRow = vertical.AsSpan(0, width);
                    outRow.Clear();
                    for (int y = 0; y < w.Length; y++)
                    {
                        ReadOnlySpan<Vector4> inRow = horizontal.AsSpan((ymin + y) * width, width);
                        float c = w[y];
                        for (int x = 0; x < outRow.Length; x++)
                        {
                            outRow[x] += inRow[x] * c;
                        }
                    }

                    FromLinearPremultiplied(outRow, target);
                }
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(horizontal);
                if (line is not null) ArrayPool<Vector4>.Shared.Return(line);
                if (vertical is not null) ArrayPool<Vector4>.Shared.Return(vertical);
            }
        });
    }

    // Output rows per band of ResampleLinear.
    private const int LinearBandRows = 32;

    private static float[] ToSingle(double[] weights)
    {
        var result = new float[weights.Length];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (float)weights[i];
        }

        return result;
    }

    /// <summary>Straight-alpha sRGB to linear light premultiplied by alpha, all in [0, 1].</summary>
    internal static void ToLinearPremultiplied(ReadOnlySpan<Rgba32> source, Span<Vector4> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            target[i] = ToLinearPremultiplied(source[i]);
        }
    }

    /// <summary>One straight-alpha sRGB pixel as linear light premultiplied by alpha.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector4 ToLinearPremultiplied(Rgba32 p)
    {
        float a = p.A / 255f;
        return new Vector4(SrgbTransfer.Decode(p.R) * a, SrgbTransfer.Decode(p.G) * a, SrgbTransfer.Decode(p.B) * a, a);
    }

    /// <summary>Linear light premultiplied by alpha back to straight-alpha sRGB; fully transparent pixels become 0.</summary>
    internal static void FromLinearPremultiplied(ReadOnlySpan<Vector4> source, Span<Rgba32> target)
    {
        for (int i = 0; i < source.Length; i++)
        {
            target[i] = FromLinearPremultiplied(source[i]);
        }
    }

    /// <summary>One premultiplied linear-light pixel back to straight-alpha sRGB.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Rgba32 FromLinearPremultiplied(Vector4 v)
    {
        byte alpha = v.W >= 1f ? (byte)255 : v.W > 0f ? (byte)((v.W * 255f) + 0.5f) : (byte)0;
        if (alpha == 0)
        {
            return default;
        }

        float scale = 1f / v.W;
        return new Rgba32(SrgbTransfer.Encode(v.X * scale), SrgbTransfer.Encode(v.Y * scale), SrgbTransfer.Encode(v.Z * scale), alpha);
    }
}
