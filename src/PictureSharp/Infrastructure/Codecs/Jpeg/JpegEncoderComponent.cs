using System.Buffers;
using System.Runtime.CompilerServices;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed partial class JpegEncoderComponent : IDisposable
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
        byte[][] quantization, int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        int width = image.Width, height = image.Height;
        var planes = new byte[components.Length][];
        for (int c = 0; c < planes.Length; c++) planes[c] = ArrayPool<byte>.Shared.Rent(width * height);
        try
        {
            ReadOnlyMemory<Rgba32> pixels = image.Frames.RootFrame.Pixels;
            const int band = 32;
            ForEach((height + band - 1) / band, (long)band * width, maxDegreeOfParallelism, cancellationToken, b =>
            {
                int start = b * band * width, end = Math.Min(height, (b + 1) * band) * width;
                ConvertColors(pixels.Span[start..end], planes, start, background);
            });

            var divisors = new JpegFdct.Divisors[components.Length];
            for (int c = 0; c < components.Length; c++) divisors[c] = new JpegFdct.Divisors(quantization[c == 0 ? 0 : 1]);
            int rows = 0;
            foreach (JpegEncoderComponent component in components) rows += component.HeightInBlocks;
            ForEach(rows, (long)components[0].WidthInBlocks * 64 * 8, maxDegreeOfParallelism, cancellationToken, row =>
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

    private static void ForEach(int count, long workPerItem, int maxDegreeOfParallelism, CancellationToken cancellationToken, Action<int> body)
    {
        if (count > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, count * workPerItem, 1 << 18))
            Parallel.For(0, count, Parallelism.Options(maxDegreeOfParallelism, cancellationToken), body);
        else
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                body(i);
            }
    }

    // The converted samples are 0–255 for any input: libjpeg's coefficients and offsets keep the sums in range.
    internal static void ConvertColors(ReadOnlySpan<Rgba32> pixels, byte[][] planes, int offset, Rgba32 background)
    {
        bool gray = planes.Length == 1;
        Span<byte> y = planes[0].AsSpan(offset, pixels.Length);
        Span<byte> cb = gray ? default : planes[1].AsSpan(offset, pixels.Length);
        Span<byte> cr = gray ? default : planes[2].AsSpan(offset, pixels.Length);
        int done = ConvertColorsSimd(pixels, y, cb, cr, background);
        ConvertColorsScalar(pixels[done..], y[done..], gray ? default : cb[done..], gray ? default : cr[done..], background);
    }

    // port: libjpeg-turbo jccolor.c rgb_ycc_convert, rgb_gray_convert; empty chroma spans convert luma only
    internal static void ConvertColorsScalar(ReadOnlySpan<Rgba32> pixels, Span<byte> y, Span<byte> cb, Span<byte> cr, Rgba32 background)
    {
        if (cb.IsEmpty)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba32 p = Composite(pixels[i], background);
                y[i] = (byte)((19595 * p.R + 38470 * p.G + 7471 * p.B + 32768) >> 16);
            }

            return;
        }

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
        Span<int> samples = stackalloc int[64];
        for (int bx = 0; bx < WidthInBlocks; bx++)
        {
            int x0 = bx * 8 * dx, y0 = by * 8 * dy;
            if (x0 + 8 * dx <= width && y0 + 8 * dy <= height)
            {
                ReadOnlySpan<byte> source = plane.AsSpan((y0 * width) + x0);
                if (!LoadBlockSimd(source, width, dx, dy, samples)) LoadBlock(source, width, dx, dy, samples);
            }
            else LoadEdgeBlock(plane, width, height, x0, y0, dx, dy, samples);

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

    // A block inside the plane: averages dx × dy samples (1 or 2 each way) with libjpeg's alternating bias.
    internal static void LoadBlock(ReadOnlySpan<byte> source, int width, int dx, int dy, Span<int> samples)
    {
        int shift = dx + dy - 2;
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<byte> row0 = source.Slice(y * dy * width, 8 * dx);
            ReadOnlySpan<byte> row1 = dy == 2 ? source.Slice(((y * 2) + 1) * width, 8 * dx) : row0;
            for (int x = 0; x < 8; x++)
            {
                int sum = dx == 2 ? row0[2 * x] + row0[(2 * x) + 1] : row0[x];
                if (dy == 2) sum += dx == 2 ? row1[2 * x] + row1[(2 * x) + 1] : row1[x];
                samples[(y * 8) + x] = ((sum + Bias(x, dx, dy)) >> shift) - 128;
            }
        }
    }

    // A block crossing the right or bottom edge replicates the last column and row.
    private static void LoadEdgeBlock(byte[] plane, int width, int height, int x0, int y0, int dx, int dy, Span<int> samples)
    {
        int shift = dx + dy - 2;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int sum = 0;
                for (int sy = 0; sy < dy; sy++)
                {
                    int row = Math.Min(y0 + (y * dy) + sy, height - 1) * width;
                    for (int sx = 0; sx < dx; sx++)
                        sum += plane[row + Math.Min(x0 + (x * dx) + sx, width - 1)];
                }

                // Sums are non-negative, so the shift equals libjpeg's division.
                samples[(y * 8) + x] = ((sum + Bias(x, dx, dy)) >> shift) - 128;
            }
        }
    }

    // port: libjpeg-turbo jcsample.c h2v1_downsample (bias 0,1,...), h2v2_downsample (1,2,...), int_downsample (half)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bias(int x, int dx, int dy) => dx == 2 ? (dy == 2 ? 1 + (x & 1) : x & 1) : dx * dy / 2;

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
