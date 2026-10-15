using System.Buffers.Binary;

namespace PictureSharp.Formats.Bmp;

internal sealed partial class BmpDecoder
{
    private static void DecodeUncompressed(ReadOnlySpan<byte> file, BmpHeader h, Rgba32[] pixels, CancellationToken cancellationToken)
    {
        int width = h.Width;
        int height = h.Height;
        int bits = h.BitCount;
        long stride = (((long)width * bits) + 31) / 32 * 4;
        long rowBytes = (((long)width * bits) + 7) / 8;

        // The padding of the last row is often omitted; everything else must be present.
        if (h.DataOffset + (stride * (height - 1)) + rowBytes > file.Length)
        {
            throw Error("The BMP pixel data is truncated.");
        }

        Rgba32[]? palette = bits <= 8 ? ReadPalette(file, h) : null;
        bool bgr = bits == 24 && (!h.HasMasks || (h.Red.Mask == 0xFF0000 && h.Green.Mask == 0xFF00 && h.Blue.Mask == 0xFF));
        bool bgrx = bits == 32 && h.Red.Mask == 0xFF0000 && h.Green.Mask == 0xFF00 && h.Blue.Mask == 0xFF
                    && h.Alpha.Mask is 0 or 0xFF000000;

        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> src = file.Slice((int)(h.DataOffset + (stride * y)), (int)rowBytes);
            int outRow = h.TopDown ? y : height - 1 - y;
            Span<Rgba32> dst = pixels.AsSpan(outRow * width, width);
            switch (bits)
            {
                case 1:
                case 2:
                case 4:
                    UnpackIndices(src, dst, bits, palette!);
                    break;
                case 8:
                    for (int x = 0; x < width; x++)
                    {
                        dst[x] = palette![src[x]];
                    }

                    break;
                case 24 when bgr:
                    for (int x = 0, i = 0; x < width; x++, i += 3)
                    {
                        dst[x] = new Rgba32(src[i + 2], src[i + 1], src[i], 255);
                    }

                    break;
                case 32 when bgrx:
                    bool alpha = h.Alpha.Mask != 0;
                    for (int x = 0, i = 0; x < width; x++, i += 4)
                    {
                        dst[x] = new Rgba32(src[i + 2], src[i + 1], src[i], alpha ? src[i + 3] : (byte)255);
                    }

                    break;
                default:
                    UnpackMasked(src, dst, bits, h);
                    break;
            }
        }
    }

    private static void UnpackIndices(ReadOnlySpan<byte> src, Span<Rgba32> dst, int bits, Rgba32[] palette)
    {
        int perByte = 8 / bits;
        int mask = (1 << bits) - 1;
        for (int x = 0; x < dst.Length; x++)
        {
            int shift = 8 - (bits * ((x % perByte) + 1));
            dst[x] = palette[(src[x / perByte] >> shift) & mask];
        }
    }

    private static void UnpackMasked(ReadOnlySpan<byte> src, Span<Rgba32> dst, int bits, BmpHeader h)
    {
        int bytes = bits / 8;
        for (int x = 0, i = 0; x < dst.Length; x++, i += bytes)
        {
            uint p = bytes switch
            {
                2 => BinaryPrimitives.ReadUInt16LittleEndian(src[i..]),
                3 => (uint)(src[i] | (src[i + 1] << 8) | (src[i + 2] << 16)),
                _ => BinaryPrimitives.ReadUInt32LittleEndian(src[i..]),
            };
            dst[x] = new Rgba32(h.Red.Extract(p, 0), h.Green.Extract(p, 0), h.Blue.Extract(p, 0), h.Alpha.Extract(p, 255));
        }
    }
}
