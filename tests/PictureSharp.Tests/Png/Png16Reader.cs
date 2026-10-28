using System.Buffers.Binary;
using System.IO.Compression;

namespace PictureSharp.Tests.Png;

/// <summary>
/// A minimal PNG reader for tests, independent of PictureSharp's decoder: <see cref="ZLibStream"/> inflation and the
/// scanline filters of the PNG specification. It reads still images of colour types 0, 2, 4 and 6 at bit depth 8 or
/// 16 (with Adam7) and returns straight RGBA samples scaled to 16 bits, plus the stored depth.
/// </summary>
internal static class Png16Reader
{
    public sealed record Result(int Width, int Height, int BitDepth, int ColorType, Rgba64[] Pixels);

    public static Result Read(byte[] file)
    {
        int pos = 8;
        int width = 0, height = 0, depth = 0, colorType = 0, interlace = 0;
        byte[]? trns = null;
        using var idat = new MemoryStream();
        while (pos < file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(pos));
            string type = System.Text.Encoding.ASCII.GetString(file, pos + 4, 4);
            ReadOnlySpan<byte> body = file.AsSpan(pos + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    interlace = body[12];
                    break;
                case "tRNS":
                    trns = body.ToArray();
                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }

            pos += 12 + length;
        }

        if (depth is not (8 or 16) || colorType is not (0 or 2 or 4 or 6))
            throw new NotSupportedException($"Test reader: depth {depth}, color type {colorType}.");
        int channels = colorType switch { 0 => 1, 2 => 3, 4 => 2, _ => 4 };
        int bytesPerSample = depth / 8;
        int bpp = channels * bytesPerSample;
        idat.Position = 0;
        using var inflater = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflater.CopyTo(raw);
        byte[] data = raw.ToArray();

        var pixels = new Rgba64[width * height];
        int offset = 0;
        (int X0, int Y0, int Dx, int Dy)[] passes = interlace == 1
            ? [(0, 0, 8, 8), (4, 0, 8, 8), (0, 4, 4, 8), (2, 0, 4, 4), (0, 2, 2, 4), (1, 0, 2, 2), (0, 1, 1, 2)]
            : [(0, 0, 1, 1)];
        Span<int> s = stackalloc int[4];
        foreach ((int x0, int y0, int dx, int dy) in passes)
        {
            int pw = x0 >= width ? 0 : (width - x0 + dx - 1) / dx;
            int ph = y0 >= height ? 0 : (height - y0 + dy - 1) / dy;
            if (pw == 0 || ph == 0)
                continue;
            int rowBytes = pw * bpp;
            byte[] previous = new byte[rowBytes];
            for (int j = 0; j < ph; j++)
            {
                int filter = data[offset];
                byte[] row = data.AsSpan(offset + 1, rowBytes).ToArray();
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = previous[i], c = i >= bpp ? previous[i - bpp] : 0;
                    int predictor = filter switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) >> 1,
                        4 => Paeth(a, b, c),
                        _ => throw new InvalidDataException("Filter type."),
                    };
                    row[i] = (byte)(row[i] + predictor);
                }

                for (int i = 0; i < pw; i++)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        int at = (i * bpp) + (c * bytesPerSample);
                        s[c] = depth == 16 ? (row[at] << 8) | row[at + 1] : row[at] * 257;
                    }

                    pixels[((y0 + (j * dy)) * width) + x0 + (i * dx)] = colorType switch
                    {
                        0 => new Rgba64((ushort)s[0], (ushort)s[0], (ushort)s[0], Key(trns, depth, s[..1]) ? (ushort)0 : ushort.MaxValue),
                        2 => new Rgba64((ushort)s[0], (ushort)s[1], (ushort)s[2], Key(trns, depth, s[..3]) ? (ushort)0 : ushort.MaxValue),
                        4 => new Rgba64((ushort)s[0], (ushort)s[0], (ushort)s[0], (ushort)s[1]),
                        _ => new Rgba64((ushort)s[0], (ushort)s[1], (ushort)s[2], (ushort)s[3]),
                    };
                }

                previous = row;
                offset += 1 + rowBytes;
            }
        }

        return new Result(width, height, depth, colorType, pixels);
    }

    private static bool Key(byte[]? trns, int depth, ReadOnlySpan<int> samples)
    {
        if (trns is null || trns.Length < 2 * samples.Length)
            return false;
        for (int c = 0; c < samples.Length; c++)
        {
            int key = (trns[2 * c] << 8) | trns[(2 * c) + 1];
            int sample = depth == 16 ? samples[c] : samples[c] / 257;
            if (key != sample)
                return false;
        }

        return true;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
