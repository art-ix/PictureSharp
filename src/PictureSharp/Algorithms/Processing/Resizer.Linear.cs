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

        // Only the source rows the vertical taps cover are converted; the bounds become relative to the first one.
        int firstRow = needVertical ? boundsV[0] : 0;
        int rowCount = needVertical ? boundsV[(height * 2) - 2] + boundsV[(height * 2) - 1] - firstRow : srcHeight;
        for (int i = 0; i < height; i++)
        {
            boundsV[i * 2] -= firstRow;
        }

        Vector4[] linear = ArrayPool<Vector4>.Shared.Rent(rowCount * srcWidth);
        Vector4[]? horizontal = needHorizontal ? ArrayPool<Vector4>.Shared.Rent(rowCount * width) : null;
        Vector4[]? vertical = needVertical ? ArrayPool<Vector4>.Shared.Rent(height * width) : null;
        try
        {
            ForRows(rowCount, srcWidth, parallel, y =>
                ToLinearPremultiplied(src.AsSpan((firstRow + y) * srcWidth, srcWidth), linear.AsSpan(y * srcWidth, srcWidth)));

            Vector4[] current = linear;
            int currentWidth = srcWidth;
            if (horizontal is not null)
            {
                float[] k = ToSingle(weightsH);
                ForRows(rowCount, (long)width * ksizeH, parallel, y =>
                {
                    ReadOnlySpan<Vector4> inRow = linear.AsSpan(y * srcWidth, srcWidth);
                    Span<Vector4> outRow = horizontal.AsSpan(y * width, width);
                    for (int xx = 0; xx < width; xx++)
                    {
                        ReadOnlySpan<float> w = k.AsSpan(xx * ksizeH, boundsH[(xx * 2) + 1]);
                        ReadOnlySpan<Vector4> px = inRow.Slice(boundsH[xx * 2], w.Length);
                        Vector4 sum = Vector4.Zero;
                        for (int x = 0; x < w.Length; x++)
                        {
                            sum += px[x] * w[x];
                        }

                        outRow[xx] = sum;
                    }
                });
                current = horizontal;
                currentWidth = width;
            }

            if (vertical is not null)
            {
                float[] k = ToSingle(weightsV);
                Vector4[] input = current;
                ForRows(height, (long)width * ksizeV, parallel, yy =>
                {
                    ReadOnlySpan<float> w = k.AsSpan(yy * ksizeV, boundsV[(yy * 2) + 1]);
                    int ymin = boundsV[yy * 2];
                    Span<Vector4> outRow = vertical.AsSpan(yy * width, width);
                    outRow.Clear();
                    for (int y = 0; y < w.Length; y++)
                    {
                        ReadOnlySpan<Vector4> inRow = input.AsSpan((ymin + y) * width, width);
                        float c = w[y];
                        for (int x = 0; x < outRow.Length; x++)
                        {
                            outRow[x] += inRow[x] * c;
                        }
                    }
                });
                current = vertical;
            }

            Vector4[] result = current;
            ForRows(height, width, parallel, y =>
                FromLinearPremultiplied(result.AsSpan(y * width, width), dst.AsSpan(y * width, width)));
        }
        finally
        {
            ArrayPool<Vector4>.Shared.Return(linear);
            if (horizontal is not null) ArrayPool<Vector4>.Shared.Return(horizontal);
            if (vertical is not null) ArrayPool<Vector4>.Shared.Return(vertical);
        }
    }

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
