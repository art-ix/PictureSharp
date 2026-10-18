using System.Buffers;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>Color space of the coded components.</summary>
internal enum JpegColorSpace
{
    Gray,
    YCbCr,
    Rgb,
    Cmyk,
    Ycck,
}

/// <summary>
/// Upsamples the component planes to full resolution and converts them to RGBA, bit-exact with libjpeg-turbo
/// (fancy upsampling, <c>jdcolor.c</c>) followed by Pillow's CMYK handling for four-component images.
/// </summary>
internal static partial class JpegColorConverter
{
    private const int ScaleBits = 16;
    private const int OneHalf = 1 << (ScaleBits - 1);

    // port: libjpeg-turbo jdcolor.c build_ycc_rgb_table
    private static readonly int[] CrToR = new int[256];
    private static readonly int[] CbToB = new int[256];
    private static readonly int[] CrToG = new int[256];
    private static readonly int[] CbToG = new int[256];

    private static int Fix(double x) => (int)((x * (1 << ScaleBits)) + 0.5);

    static JpegColorConverter()
    {
        for (int i = 0, x = -128; i < 256; i++, x++)
        {
            CrToR[i] = ((Fix(1.40200) * x) + OneHalf) >> ScaleBits;
            CbToB[i] = ((Fix(1.77200) * x) + OneHalf) >> ScaleBits;
            CrToG[i] = -Fix(0.71414) * x;
            CbToG[i] = (-Fix(0.34414) * x) + OneHalf;
        }
    }

    private enum Upsampling
    {
        None,
        H2V1Fancy,
        H1V2Fancy,
        H2V2Fancy,
        Replicate,
    }

    /// <summary>Throws <see cref="NotSupportedException"/> for sampling factors that are not integral ratios.</summary>
    public static void Validate(JpegFrame frame)
    {
        foreach (JpegComponent c in frame.Components)
        {
            if (frame.MaxH % c.H != 0 || frame.MaxV % c.V != 0)
            {
                throw new NotSupportedException("JPEG images with fractional sampling ratios are not supported.");
            }
        }
    }

    /// <summary>Converts the planes to RGBA; bands of rows are independent and may run in parallel.</summary>
    public static Rgba32[] Convert(JpegFrame frame, JpegColorSpace colorSpace, int maxDegreeOfParallelism = 1,
        CancellationToken cancellationToken = default)
    {
        int width = frame.OutputWidth;
        int height = frame.OutputHeight;
        Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
        const int band = 16;
        int bands = (height + band - 1) / band;
        if (bands > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)width * height, 1 << 17))
        {
            Parallel.For(0, bands, Parallelism.Options(maxDegreeOfParallelism, cancellationToken),
                b => ConvertRows(frame, colorSpace, pixels, b * band, Math.Min(height, (b + 1) * band)));
        }
        else
        {
            for (int b = 0; b < bands; b++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConvertRows(frame, colorSpace, pixels, b * band, Math.Min(height, (b + 1) * band));
            }
        }

        return pixels;
    }

    private static void ConvertRows(JpegFrame frame, JpegColorSpace colorSpace, Rgba32[] pixels, int firstRow, int endRow)
    {
        int width = frame.OutputWidth;
        JpegComponent[] comps = frame.Components;
        int n = colorSpace == JpegColorSpace.Gray ? 1 : comps.Length;
        var modes = new Upsampling[n];
        int bufferLength = width + 16;
        for (int i = 0; i < n; i++)
        {
            modes[i] = ModeOf(frame, comps[i]);
            bufferLength = Math.Max(bufferLength, (comps[i].DownsampledWidth * comps[i].UpsampleH) + 16);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferLength * n);
        Span<int> cachedRow = stackalloc int[n];
        cachedRow.Fill(-1);
        try
        {
            for (int y = firstRow; y < endRow; y++)
            {
                Span<Rgba32> dst = pixels.AsSpan(y * width, width);
                ReadOnlySpan<byte> c0 = Row(comps[0], modes[0], y, buffer.AsSpan(0, bufferLength), ref cachedRow[0])[..width];
                if (n == 1)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte v = c0[x];
                        dst[x] = new Rgba32(v, v, v, 255);
                    }

                    continue;
                }

                ReadOnlySpan<byte> c1 = Row(comps[1], modes[1], y, buffer.AsSpan(bufferLength, bufferLength), ref cachedRow[1])[..width];
                ReadOnlySpan<byte> c2 = Row(comps[2], modes[2], y, buffer.AsSpan(2 * bufferLength, bufferLength), ref cachedRow[2])[..width];
                switch (colorSpace)
                {
                    case JpegColorSpace.YCbCr:
                        YccToRgb(c0, c1, c2, dst);
                        break;
                    case JpegColorSpace.Rgb:
                        for (int x = 0; x < width; x++)
                        {
                            dst[x] = new Rgba32(c0[x], c1[x], c2[x], 255);
                        }

                        break;
                    default:
                        ReadOnlySpan<byte> c3 = Row(comps[3], modes[3], y, buffer.AsSpan(3 * bufferLength, bufferLength), ref cachedRow[3])[..width];
                        CmykToRgb(c0, c1, c2, c3, colorSpace == JpegColorSpace.Ycck, dst);
                        break;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // port: libjpeg-turbo jdsample.c jinit_upsampler (method selection with do_fancy_upsampling)
    private static Upsampling ModeOf(JpegFrame frame, JpegComponent c)
    {
        // libjpeg has no context rows at 1/8 scale, so it upsamples by replication there.
        bool fancy = frame.MinBlockSize > 1;
        return (c.UpsampleH, c.UpsampleV) switch
        {
            (1, 1) => Upsampling.None,
            (2, 1) when fancy && c.DownsampledWidth > 2 => Upsampling.H2V1Fancy,
            (1, 2) when fancy => Upsampling.H1V2Fancy,
            (2, 2) when fancy && c.DownsampledWidth > 2 => Upsampling.H2V2Fancy,
            _ => Upsampling.Replicate,
        };
    }

    /// <summary>
    /// Returns output row <paramref name="y"/> of a component at full resolution. Rows above the image and
    /// below its last real sample row replicate the edge row, as libjpeg's context rows do.
    /// </summary>
    private static ReadOnlySpan<byte> Row(JpegComponent c, Upsampling mode, int y, Span<byte> buffer, ref int cachedRow)
    {
        byte[] plane = c.Plane;
        int stride = c.PlaneStride;
        int dw = c.DownsampledWidth;
        switch (mode)
        {
            case Upsampling.None:
                return plane.AsSpan(y * stride, dw);

            case Upsampling.H2V1Fancy:
                H2V1(plane.AsSpan(y * stride, dw), buffer);
                return buffer;

            case Upsampling.H1V2Fancy:
                {
                    (int nearRow, int farRow) = Neighbours(c, y);
                    ReadOnlySpan<byte> near = plane.AsSpan(nearRow * stride, dw);
                    ReadOnlySpan<byte> far = plane.AsSpan(farRow * stride, dw);
                    H1V2(near, far, (y & 1) != 0 ? 2 : 1, buffer);
                    return buffer;
                }

            case Upsampling.H2V2Fancy:
                {
                    (int nearRow, int farRow) = Neighbours(c, y);
                    H2V2(plane.AsSpan(nearRow * stride, dw), plane.AsSpan(farRow * stride, dw), buffer);
                    return buffer;
                }

            default:
                {
                    // port: libjpeg-turbo jdsample.c int_upsample (also h2v1_upsample/h2v2_upsample)
                    int hf = c.UpsampleH;
                    int row = y / c.UpsampleV;
                    if (cachedRow != row)
                    {
                        ReadOnlySpan<byte> src = plane.AsSpan(row * stride, dw);
                        for (int x = 0, o = 0; x < dw; x++, o += hf)
                        {
                            buffer.Slice(o, hf).Fill(src[x]);
                        }

                        cachedRow = row;
                    }

                    return buffer;
                }
        }
    }

    /// <summary>Input row nearest to output row <paramref name="y"/> of a 2x vertically subsampled component, and the next nearest.</summary>
    private static (int Near, int Far) Neighbours(JpegComponent c, int y)
    {
        int row = y >> 1;
        int far = (y & 1) != 0 ? Math.Min(row + 1, c.DownsampledHeight - 1) : Math.Max(row - 1, 0);
        return (row, far);
    }

    // port: libjpeg-turbo jdsample.c h1v2_fancy_upsample (one output row)
    internal static void H1V2(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int bias, Span<byte> dst, bool simd = true)
    {
        for (int x = simd ? H1V2Simd(near, far, bias, dst) : 0; x < near.Length; x++)
        {
            dst[x] = (byte)(((near[x] * 3) + far[x] + bias) >> 2);
        }
    }

    // port: libjpeg-turbo jdsample.c h2v1_fancy_upsample
    internal static void H2V1(ReadOnlySpan<byte> src, Span<byte> dst, bool simd = true)
    {
        int n = src.Length;
        int v = src[0];
        dst[0] = (byte)v;
        dst[1] = (byte)(((v * 3) + src[1] + 2) >> 2);
        for (int x = 1 + (simd ? H2V1Simd(src, dst) : 0); x < n - 1; x++)
        {
            v = src[x] * 3;
            dst[2 * x] = (byte)((v + src[x - 1] + 1) >> 2);
            dst[(2 * x) + 1] = (byte)((v + src[x + 1] + 2) >> 2);
        }

        v = src[n - 1];
        dst[(2 * n) - 2] = (byte)(((v * 3) + src[n - 2] + 1) >> 2);
        dst[(2 * n) - 1] = (byte)v;
    }

    // port: libjpeg-turbo jdsample.c h2v2_fancy_upsample (one output row)
    internal static void H2V2(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, Span<byte> dst, bool simd = true)
    {
        int n = near.Length;
        int thisSum = (near[0] * 3) + far[0];
        dst[0] = (byte)(((thisSum * 4) + 8) >> 4);
        dst[1] = (byte)(((thisSum * 3) + (near[1] * 3) + far[1] + 7) >> 4);
        for (int x = 1 + (simd ? H2V2Simd(near, far, dst) : 0); x < n - 1; x++)
        {
            thisSum = (near[x] * 3) + far[x];
            dst[2 * x] = (byte)(((thisSum * 3) + (near[x - 1] * 3) + far[x - 1] + 8) >> 4);
            dst[(2 * x) + 1] = (byte)(((thisSum * 3) + (near[x + 1] * 3) + far[x + 1] + 7) >> 4);
        }

        thisSum = (near[n - 1] * 3) + far[n - 1];
        dst[(2 * n) - 2] = (byte)(((thisSum * 3) + (near[n - 2] * 3) + far[n - 2] + 8) >> 4);
        dst[(2 * n) - 1] = (byte)(((thisSum * 4) + 7) >> 4);
    }

    // port: libjpeg-turbo jdcolor.c ycc_rgb_convert
    internal static void YccToRgb(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> cbRow, ReadOnlySpan<byte> crRow, Span<Rgba32> dst, bool simd = true)
    {
        int[] crr = CrToR, cbb = CbToB, crg = CrToG, cbg = CbToG;
        for (int x = simd ? YccToRgbSimd(yRow, cbRow, crRow, dst) : 0; x < dst.Length; x++)
        {
            int y = yRow[x];
            int cb = cbRow[x];
            int cr = crRow[x];
            dst[x] = new Rgba32(
                Clamp(y + crr[cr]),
                Clamp(y + ((cbg[cb] + crg[cr]) >> ScaleBits)),
                Clamp(y + cbb[cb]),
                255);
        }
    }

    /// <summary>
    /// Four-component images use Adobe's inverted CMYK, as Pillow always assumes; YCCK is first converted to
    /// CMYK like libjpeg's <c>ycck_cmyk_convert</c>. CMYK to RGB follows Pillow's <c>cmyk2rgb</c>.
    /// </summary>
    private static void CmykToRgb(ReadOnlySpan<byte> c0, ReadOnlySpan<byte> c1, ReadOnlySpan<byte> c2, ReadOnlySpan<byte> kRow,
                                  bool ycck, Span<Rgba32> dst)
    {
        int[] crr = CrToR, cbb = CbToB, crg = CrToG, cbg = CbToG;
        for (int x = 0; x < dst.Length; x++)
        {
            int c, m, ye;
            if (ycck)
            {
                int y = c0[x];
                int cb = c1[x];
                int cr = c2[x];
                c = Clamp(255 - (y + crr[cr]));
                m = Clamp(255 - (y + ((cbg[cb] + crg[cr]) >> ScaleBits)));
                ye = Clamp(255 - (y + cbb[cb]));
            }
            else
            {
                c = c0[x];
                m = c1[x];
                ye = c2[x];
            }

            // Inverted storage: the stored K is 255 - k, i.e. the "nk" of Pillow's conversion.
            int nk = kRow[x];
            dst[x] = new Rgba32(
                Clamp(nk - MulDiv255(255 - c, nk)),
                Clamp(nk - MulDiv255(255 - m, nk)),
                Clamp(nk - MulDiv255(255 - ye, nk)),
                255);
        }
    }

    private static int MulDiv255(int a, int b)
    {
        int t = (a * b) + 128;
        return ((t >> 8) + t) >> 8;
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);
}
