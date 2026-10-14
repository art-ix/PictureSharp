using System.Buffers;
using System.Runtime.CompilerServices;
using RasterSharp.Internal;

namespace RasterSharp.Formats.Jpeg;

internal sealed class JpegEncoderComponent : IDisposable
{
    public int Horizontal { get; }
    public int Vertical { get; }
    public int WidthInBlocks { get; }
    public int HeightInBlocks { get; }
    public int PaddedWidth { get; }
    public int PaddedHeight { get; }
    private readonly short[] _coefficients;

    public JpegEncoderComponent(int width, int height, int h, int v, int maxH, int maxV)
    {
        Horizontal = h;
        Vertical = v;
        WidthInBlocks = (width * h + maxH * 8 - 1) / (maxH * 8);
        HeightInBlocks = (height * v + maxV * 8 - 1) / (maxV * 8);
        PaddedWidth = (WidthInBlocks + h - 1) / h * h;
        PaddedHeight = (HeightInBlocks + v - 1) / v * v;
        _coefficients = ArrayPool<short>.Shared.Rent(checked(PaddedWidth * PaddedHeight * 64));
    }

    /// <summary>The quantized coefficients of a block in zigzag order.</summary>
    public Span<short> Block(int x, int y) => _coefficients.AsSpan((y * PaddedWidth + x) * 64, 64);

    /// <summary>Converts, downsamples, transforms and quantizes every component. Blocks are independent, so rows of blocks run in parallel.</summary>
    // port: libjpeg-turbo jccolor.c rgb_ycc_convert; jcsample.c h2v1_downsample,
    // h2v2_downsample, int_downsample; jccoefct.c compress_first_pass
    public static void PrepareAll(Image image, JpegEncoderComponent[] components, int maxH, int maxV, Rgba32 background,
        byte[][] quantization, int maxDegreeOfParallelism)
    {
        int width = image.Width, height = image.Height;
        var planes = new byte[components.Length][];
        for (int c = 0; c < planes.Length; c++) planes[c] = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            ReadOnlyMemory<Rgba32> pixels = image.Frames.RootFrame.Pixels;
            const int band = 32;
            ForEach((height + band - 1) / band, (long)band * width, maxDegreeOfParallelism, b =>
            {
                int start = b * band * width, end = Math.Min(height, (b + 1) * band) * width;
                ConvertColors(pixels.Span[start..end], planes, start, background);
            });

            var divisors = new JpegFdct.Divisors[components.Length];
            for (int c = 0; c < components.Length; c++) divisors[c] = new JpegFdct.Divisors(quantization[c == 0 ? 0 : 1]);
            int rows = 0;
            foreach (JpegEncoderComponent component in components) rows += component.HeightInBlocks;
            ForEach(rows, (long)components[0].WidthInBlocks * 64 * 8, maxDegreeOfParallelism, row =>
            {
                int c = 0;
                while (row >= components[c].HeightInBlocks) row -= components[c++].HeightInBlocks;
                components[c].PrepareRow(planes[c], width, height, row, maxH, maxV, divisors[c]);
            });

            foreach (JpegEncoderComponent component in components) component.PadRows();
        }
        finally
        {
            foreach (byte[] plane in planes) ArrayPool<byte>.Shared.Return(plane);
        }
    }

    private static void ForEach(int count, long workPerItem, int maxDegreeOfParallelism, Action<int> body)
    {
        if (count > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, count * workPerItem, 1 << 18))
            Parallel.For(0, count, Parallelism.Options(maxDegreeOfParallelism), body);
        else
            for (int i = 0; i < count; i++) body(i);
    }

    // The converted samples are 0–255 for any input: libjpeg's coefficients and offsets keep the sums in range.
    private static void ConvertColors(ReadOnlySpan<Rgba32> pixels, byte[][] planes, int offset, Rgba32 background)
    {
        Span<byte> y = planes[0].AsSpan(offset, pixels.Length);
        if (planes.Length == 1)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba32 p = Composite(pixels[i], background);
                y[i] = (byte)((19595 * p.R + 38470 * p.G + 7471 * p.B + 32768) >> 16);
            }

            return;
        }

        Span<byte> cb = planes[1].AsSpan(offset, pixels.Length);
        Span<byte> cr = planes[2].AsSpan(offset, pixels.Length);
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = Composite(pixels[i], background);
            y[i] = (byte)((19595 * p.R + 38470 * p.G + 7471 * p.B + 32768) >> 16);
            cb[i] = (byte)((-11059 * p.R - 21709 * p.G + 32768 * p.B + (128 << 16) + 32767) >> 16);
            cr[i] = (byte)((32768 * p.R - 27439 * p.G - 5329 * p.B + (128 << 16) + 32767) >> 16);
        }
    }

    // One row of blocks, plus the padding blocks at its right (they repeat the DC of the block before).
    private void PrepareRow(byte[] plane, int width, int height, int by, int maxH, int maxV, JpegFdct.Divisors divisors)
    {
        int dx = maxH / Horizontal, dy = maxV / Vertical;
        int shift = dx * dy == 4 ? 2 : dx * dy == 2 ? 1 : 0;
        Span<int> samples = stackalloc int[64];
        for (int bx = 0; bx < WidthInBlocks; bx++)
        {
            int x0 = bx * 8 * dx, y0 = by * 8 * dy;
            bool inside = x0 + 8 * dx <= width && y0 + 8 * dy <= height;
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int sum = 0;
                    for (int sy = 0; sy < dy; sy++)
                    {
                        int row = (inside ? y0 + y * dy + sy : Math.Min(y0 + y * dy + sy, height - 1)) * width;
                        for (int sx = 0; sx < dx; sx++)
                            sum += plane[row + (inside ? x0 + x * dx + sx : Math.Min(x0 + x * dx + sx, width - 1))];
                    }

                    // Sums are non-negative, so the shift equals libjpeg's division.
                    int bias = dx == 2 ? (dy == 2 ? 1 + (x & 1) : x & 1) : dx * dy / 2;
                    samples[y * 8 + x] = ((sum + bias) >> shift) - 128;
                }
            }

            JpegFdct.Transform(samples);
            JpegFdct.QuantizeZigzag(samples, divisors, Block(bx, by));
        }

        for (int bx = WidthInBlocks; bx < PaddedWidth; bx++)
        {
            Span<short> block = Block(bx, by);
            block.Clear();
            block[0] = Block(bx - 1, by)[0];
        }
    }

    private void PadRows()
    {
        for (int by = HeightInBlocks; by < PaddedHeight; by++)
        {
            for (int bx = 0; bx < PaddedWidth; bx++)
            {
                Span<short> block = Block(bx, by);
                block.Clear();
                block[0] = Block(bx / Horizontal * Horizontal + Horizontal - 1, by - 1)[0];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Rgba32 Composite(Rgba32 p, Rgba32 background)
    {
        if (p.A == 255) return p;
        int inverse = 255 - p.A;
        return new Rgba32((byte)((p.R * p.A + background.R * inverse + 127) / 255),
            (byte)((p.G * p.A + background.G * inverse + 127) / 255),
            (byte)((p.B * p.A + background.B * inverse + 127) / 255), 255);
    }

    public void Dispose() => ArrayPool<short>.Shared.Return(_coefficients);
}
