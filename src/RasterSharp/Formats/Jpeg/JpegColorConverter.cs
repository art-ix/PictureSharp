using System.Buffers;
using RasterSharp.Internal;

namespace RasterSharp.Formats.Jpeg;

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
internal static class JpegColorConverter
{
    private const int ScaleBits = 16;
    private const int OneHalf = 1 << (ScaleBits - 1);

    // port: libjpeg-turbo jdcolor.c build_ycc_rgb_table
    private static readonly int[] CrToR = new int[256];
    private static readonly int[] CbToB = new int[256];
    private static readonly int[] CrToG = new int[256];
    private static readonly int[] CbToG = new int[256];

    static JpegColorConverter()
    {
        static int Fix(double x) => (int)((x * (1 << ScaleBits)) + 0.5);
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
    public static Rgba32[] Convert(JpegFrame frame, JpegColorSpace colorSpace, int maxDegreeOfParallelism = 1)
    {
        int width = frame.Width;
        int height = frame.Height;
        Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
        const int band = 16;
        int bands = (height + band - 1) / band;
        if (bands > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)width * height, 1 << 17))
        {
            Parallel.For(0, bands, Parallelism.Options(maxDegreeOfParallelism),
                b => ConvertRows(frame, colorSpace, pixels, b * band, Math.Min(height, (b + 1) * band)));
        }
        else
        {
            ConvertRows(frame, colorSpace, pixels, 0, height);
        }

        return pixels;
    }

    private static void ConvertRows(JpegFrame frame, JpegColorSpace colorSpace, Rgba32[] pixels, int firstRow, int endRow)
    {
        int width = frame.Width;
        JpegComponent[] comps = frame.Components;
        int n = colorSpace == JpegColorSpace.Gray ? 1 : comps.Length;
        var modes = new Upsampling[n];
        int bufferLength = width + 16;
        for (int i = 0; i < n; i++)
        {
            modes[i] = ModeOf(frame, comps[i]);
            bufferLength = Math.Max(bufferLength, (comps[i].DownsampledWidth * frame.MaxH / comps[i].H) + 16);
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferLength * n);
        Span<int> cachedRow = stackalloc int[n];
        cachedRow.Fill(-1);
        try
        {
            for (int y = firstRow; y < endRow; y++)
            {
                Span<Rgba32> dst = pixels.AsSpan(y * width, width);
                ReadOnlySpan<byte> c0 = Row(frame, comps[0], modes[0], y, buffer.AsSpan(0, bufferLength), ref cachedRow[0])[..width];
                if (n == 1)
                {
                    for (int x = 0; x < width; x++)
                    {
                        byte v = c0[x];
                        dst[x] = new Rgba32(v, v, v, 255);
                    }

                    continue;
                }

                ReadOnlySpan<byte> c1 = Row(frame, comps[1], modes[1], y, buffer.AsSpan(bufferLength, bufferLength), ref cachedRow[1])[..width];
                ReadOnlySpan<byte> c2 = Row(frame, comps[2], modes[2], y, buffer.AsSpan(2 * bufferLength, bufferLength), ref cachedRow[2])[..width];
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
                        ReadOnlySpan<byte> c3 = Row(frame, comps[3], modes[3], y, buffer.AsSpan(3 * bufferLength, bufferLength), ref cachedRow[3])[..width];
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
        int hf = frame.MaxH / c.H;
        int vf = frame.MaxV / c.V;
        return (hf, vf) switch
        {
            (1, 1) => Upsampling.None,
            (2, 1) when c.DownsampledWidth > 2 => Upsampling.H2V1Fancy,
            (1, 2) => Upsampling.H1V2Fancy,
            (2, 2) when c.DownsampledWidth > 2 => Upsampling.H2V2Fancy,
            _ => Upsampling.Replicate,
        };
    }

    /// <summary>
    /// Returns output row <paramref name="y"/> of a component at full resolution. Rows above the image and
    /// below its last real sample row replicate the edge row, as libjpeg's context rows do.
    /// </summary>
    private static ReadOnlySpan<byte> Row(JpegFrame frame, JpegComponent c, Upsampling mode, int y, Span<byte> buffer, ref int cachedRow)
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
                // port: libjpeg-turbo jdsample.c h1v2_fancy_upsample
                int bias = (y & 1) != 0 ? 2 : 1;
                for (int x = 0; x < dw; x++)
                {
                    buffer[x] = (byte)(((near[x] * 3) + far[x] + bias) >> 2);
                }

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
                int hf = frame.MaxH / c.H;
                int row = y / (frame.MaxV / c.V);
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

    // port: libjpeg-turbo jdsample.c h2v1_fancy_upsample
    private static void H2V1(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        int n = src.Length;
        int o = 0;
        int v = src[0];
        dst[o++] = (byte)v;
        dst[o++] = (byte)(((v * 3) + src[1] + 2) >> 2);
        for (int x = 1; x < n - 1; x++)
        {
            v = src[x] * 3;
            dst[o++] = (byte)((v + src[x - 1] + 1) >> 2);
            dst[o++] = (byte)((v + src[x + 1] + 2) >> 2);
        }

        v = src[n - 1];
        dst[o++] = (byte)(((v * 3) + src[n - 2] + 1) >> 2);
        dst[o] = (byte)v;
    }

    // port: libjpeg-turbo jdsample.c h2v2_fancy_upsample (one output row)
    private static void H2V2(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, Span<byte> dst)
    {
        int n = near.Length;
        int o = 0;
        int thisSum = (near[0] * 3) + far[0];
        int nextSum = (near[1] * 3) + far[1];
        dst[o++] = (byte)(((thisSum * 4) + 8) >> 4);
        dst[o++] = (byte)(((thisSum * 3) + nextSum + 7) >> 4);
        int lastSum = thisSum;
        thisSum = nextSum;
        for (int x = 2; x < n; x++)
        {
            nextSum = (near[x] * 3) + far[x];
            dst[o++] = (byte)(((thisSum * 3) + lastSum + 8) >> 4);
            dst[o++] = (byte)(((thisSum * 3) + nextSum + 7) >> 4);
            lastSum = thisSum;
            thisSum = nextSum;
        }

        dst[o++] = (byte)(((thisSum * 3) + lastSum + 8) >> 4);
        dst[o] = (byte)(((thisSum * 4) + 7) >> 4);
    }

    // port: libjpeg-turbo jdcolor.c ycc_rgb_convert
    private static void YccToRgb(ReadOnlySpan<byte> yRow, ReadOnlySpan<byte> cbRow, ReadOnlySpan<byte> crRow, Span<Rgba32> dst)
    {
        int[] crr = CrToR, cbb = CbToB, crg = CrToG, cbg = CbToG;
        for (int x = 0; x < dst.Length; x++)
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
