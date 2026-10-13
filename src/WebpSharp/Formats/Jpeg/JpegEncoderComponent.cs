using System.Buffers;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace WebpSharp.Formats.Jpeg;

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

    public Span<short> Block(int x, int y) => _coefficients.AsSpan((y * PaddedWidth + x) * 64, 64);

    // port: libjpeg-turbo jccolor.c rgb_ycc_convert; jcsample.c h2v1_downsample,
    // h2v2_downsample, int_downsample; jccoefct.c compress_first_pass
    public void Prepare(Image image, int component, int maxH, int maxV, Color background, ReadOnlySpan<byte> quantization)
    {
        int dx = maxH / Horizontal, dy = maxV / Vertical;
        Span<int> samples = stackalloc int[64];
        ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan();
        for (int by = 0; by < HeightInBlocks; by++)
        {
            for (int bx = 0; bx < WidthInBlocks; bx++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int sum = 0;
                        for (int sy = 0; sy < dy; sy++)
                        {
                            int row = Math.Min((by * 8 + y) * dy + sy, image.Height - 1) * image.Width;
                            for (int sx = 0; sx < dx; sx++)
                            {
                                Rgba32 p = Composite(pixels[row + Math.Min((bx * 8 + x) * dx + sx, image.Width - 1)], background);
                                sum += component switch
                                {
                                    0 => (19595 * p.R + 38470 * p.G + 7471 * p.B + 32768) >> 16,
                                    1 => (-11059 * p.R - 21709 * p.G + 32768 * p.B + (128 << 16) + 32767) >> 16,
                                    _ => (32768 * p.R - 27439 * p.G - 5329 * p.B + (128 << 16) + 32767) >> 16,
                                };
                            }
                        }

                        int bias = dx == 2 ? (dy == 2 ? 1 + (x & 1) : x & 1) : dx * dy / 2;
                        samples[y * 8 + x] = (sum + bias) / (dx * dy) - 128;
                    }
                }

                JpegFdct.Transform(samples);
                JpegFdct.Quantize(samples, quantization, Block(bx, by));
            }

            for (int bx = WidthInBlocks; bx < PaddedWidth; bx++)
            {
                Span<short> block = Block(bx, by);
                block.Clear();
                block[0] = Block(bx - 1, by)[0];
            }
        }

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
    internal static Rgba32 Composite(Rgba32 p, Color background)
    {
        if (p.A == 255) return p;
        int inverse = 255 - p.A;
        return new Rgba32((byte)((p.R * p.A + background.R * inverse + 127) / 255),
            (byte)((p.G * p.A + background.G * inverse + 127) / 255),
            (byte)((p.B * p.A + background.B * inverse + 127) / 255), 255);
    }

    public void Dispose() => ArrayPool<short>.Shared.Return(_coefficients);
}
