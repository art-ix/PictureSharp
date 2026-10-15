using System.Buffers.Binary;

namespace PictureSharp.Formats.Bmp;

internal sealed partial class BmpDecoder
{
    // ------------------------------------------------------------------ run-length encoded

    /// <summary>
    /// Decodes BI_RLE8/BI_RLE4. Pixels skipped by delta or end-of-line codes keep palette index 0 (as in Pillow);
    /// pixels beyond the row width are dropped. A stream that ends early leaves the remaining rows at index 0.
    /// </summary>
    private static void DecodeRle(ReadOnlySpan<byte> file, BmpHeader h, Rgba32[] palette, Rgba32[] pixels)
    {
        int width = h.Width;
        int height = h.Height;
        bool rle4 = h.Compression == BmpCompression.Rle4;
        byte[] indices = new byte[width * height];
        ReadOnlySpan<byte> s = file[(int)h.DataOffset..];
        int pos = 0;
        int x = 0;
        int y = 0;
        while (pos + 1 < s.Length && y < height)
        {
            int count = s[pos];
            int value = s[pos + 1];
            pos += 2;
            if (count > 0)
            {
                // Encoded run: one byte (RLE8) or two alternating nibbles (RLE4).
                for (int i = 0; i < count; i++, x++)
                {
                    if (x < width)
                    {
                        indices[(y * width) + x] = (byte)(rle4 ? ((i & 1) == 0 ? value >> 4 : value & 15) : value);
                    }
                }
            }
            else if (value == 0)
            {
                x = 0;
                y++;
            }
            else if (value == 1)
            {
                break;
            }
            else if (value == 2)
            {
                if (pos + 1 >= s.Length)
                {
                    break;
                }

                x += s[pos];
                y += s[pos + 1];
                pos += 2;
            }
            else
            {
                // Absolute run of `value` pixels, padded to a 16-bit boundary.
                int bytes = rle4 ? (value + 1) / 2 : value;
                int available = Math.Min(bytes, s.Length - pos);
                int pixelsAvailable = rle4 ? Math.Min(value, available * 2) : available;
                for (int i = 0; i < pixelsAvailable; i++, x++)
                {
                    if (x < width)
                    {
                        int b = s[pos + (rle4 ? i >> 1 : i)];
                        indices[(y * width) + x] = (byte)(rle4 ? ((i & 1) == 0 ? b >> 4 : b & 15) : b);
                    }
                }

                if (available < bytes)
                {
                    break;
                }

                pos += bytes + (bytes & 1);
            }

            x = Math.Min(x, width);  // keeps x bounded on hostile streams of long runs
        }

        for (int row = 0; row < height; row++)
        {
            int outRow = h.TopDown ? row : height - 1 - row;
            ReadOnlySpan<byte> src = indices.AsSpan(row * width, width);
            Span<Rgba32> dst = pixels.AsSpan(outRow * width, width);
            for (int i = 0; i < width; i++)
            {
                dst[i] = palette[src[i]];
            }
        }
    }

    /// <summary>Decodes OS/2 RLE24 (three-byte BGR pixels in place of palette indices). Skipped pixels are black.</summary>
    private static void DecodeRle24(ReadOnlySpan<byte> file, BmpHeader h, Rgba32[] pixels)
    {
        int width = h.Width;
        int height = h.Height;
        pixels.AsSpan().Fill(Black);
        ReadOnlySpan<byte> s = file[(int)h.DataOffset..];
        int pos = 0;
        int x = 0;
        int y = 0;
        while (pos + 1 < s.Length && y < height)
        {
            int count = s[pos];
            int value = s[pos + 1];
            if (count > 0)
            {
                if (pos + 4 > s.Length)
                {
                    break;
                }

                var color = new Rgba32(s[pos + 3], s[pos + 2], s[pos + 1], 255);
                pos += 4;
                for (int i = 0; i < count; i++, x++)
                {
                    if (x < width)
                    {
                        pixels[Row(y) + x] = color;
                    }
                }

                x = Math.Min(x, width);
                continue;
            }

            pos += 2;
            if (value == 0)
            {
                x = 0;
                y++;
            }
            else if (value == 1)
            {
                break;
            }
            else if (value == 2)
            {
                if (pos + 1 >= s.Length)
                {
                    break;
                }

                x += s[pos];
                y += s[pos + 1];
                pos += 2;
            }
            else
            {
                int bytes = value * 3;
                int available = Math.Min(bytes, s.Length - pos) / 3;
                for (int i = 0; i < available; i++, x++)
                {
                    if (x < width)
                    {
                        int p = pos + (3 * i);
                        pixels[Row(y) + x] = new Rgba32(s[p + 2], s[p + 1], s[p], 255);
                    }
                }

                if (available < value)
                {
                    break;
                }

                pos += bytes + (bytes & 1);
            }

            x = Math.Min(x, width);
        }

        int Row(int line) => (h.TopDown ? line : height - 1 - line) * width;
    }
}
