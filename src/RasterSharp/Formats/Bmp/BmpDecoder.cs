using System.Buffers.Binary;

namespace RasterSharp.Formats.Bmp;

/// <summary>Decoder for Windows and OS/2 bitmaps.</summary>
internal sealed class BmpDecoder : IImageDecoder
{
    private static readonly Rgba32 Black = new(0, 0, 0, 255);

    public static BmpDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Bmp;

    private static ImageFormatException Error(string message) => new(ImageFormat.Bmp, message);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        BmpHeader h = BmpHeader.Parse(data.Span);
        bool hasAlpha = h.HasAlpha;
        if (h.Compression == BmpCompression.Embedded)
        {
            hasAlpha = EmbeddedDecoder(data.Span, h).Identify(EmbeddedData(data, h), options).HasAlpha;
        }

        (float dpiX, float dpiY) = Resolution(h);
        return new ImageInfo
        {
            Format = ImageFormat.Bmp,
            Width = h.Width,
            Height = h.Height,
            HasAlpha = hasAlpha,
            HorizontalResolution = dpiX,
            VerticalResolution = dpiY,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> file = data.Span;
        BmpHeader h = BmpHeader.Parse(file);
        options.ValidateDimensions(ImageFormat.Bmp, h.Width, h.Height);

        Image image;
        if (h.Compression == BmpCompression.Embedded)
        {
            image = DecodeEmbedded(data, h, options);
        }
        else
        {
            Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(h.Width * h.Height);
            switch (h.Compression)
            {
                case BmpCompression.Rle8:
                case BmpCompression.Rle4:
                    DecodeRle(file, h, ReadPalette(file, h), pixels);
                    break;
                case BmpCompression.Rle24:
                    DecodeRle24(file, h, pixels);
                    break;
                default:
                    DecodeUncompressed(file, h, pixels);
                    break;
            }

            List<ImageFrame> frames = [new ImageFrame(h.Width, h.Height, pixels)];
            image = new Image(frames) { HasAlpha = h.HasAlpha };
        }

        image.SourceFormat = ImageFormat.Bmp;
        if (h.Compression != BmpCompression.Embedded || (h.XPelsPerMeter > 0 && h.YPelsPerMeter > 0))
        {
            (float dpiX, float dpiY) = Resolution(h);
            (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution) = (dpiX, dpiY);
        }

        if (!options.IgnoreMetadata && h.IccOffset >= 0)
        {
            image.Metadata.IccProfile = file.Slice(h.IccOffset, h.IccLength).ToArray();
        }

        return image;
    }

    private static (float X, float Y) Resolution(BmpHeader h) =>
        h.XPelsPerMeter > 0 && h.YPelsPerMeter > 0
            ? ((float)(h.XPelsPerMeter * 0.0254), (float)(h.YPelsPerMeter * 0.0254))
            : (96f, 96f);

    // ------------------------------------------------------------------ embedded PNG/JPEG

    private static ReadOnlyMemory<byte> EmbeddedData(ReadOnlyMemory<byte> data, BmpHeader h)
    {
        int start = (int)h.DataOffset;
        int available = data.Length - start;
        int length = h.ImageSize > 0 && h.ImageSize < available ? (int)h.ImageSize : available;
        return data.Slice(start, length);
    }

    private static IImageDecoder EmbeddedDecoder(ReadOnlySpan<byte> file, BmpHeader h)
    {
        ImageFormat expected = h.RawCompression == BmpConstants.Png ? ImageFormat.Png : ImageFormat.Jpeg;
        ImageFormat? actual = FormatDetector.Detect(file[(int)h.DataOffset..]);
        if (actual != expected)
        {
            throw Error($"The BMP does not contain the {expected.Name} data its header declares.");
        }

        return CodecRegistry.GetDecoder(expected)!;
    }

    private static Image DecodeEmbedded(ReadOnlyMemory<byte> data, BmpHeader h, DecoderOptions options)
    {
        return EmbeddedDecoder(data.Span, h).Decode(EmbeddedData(data, h), options);
    }

    // ------------------------------------------------------------------ palette

    private static Rgba32[] ReadPalette(ReadOnlySpan<byte> file, BmpHeader h)
    {
        // Indices beyond the stored palette map to black.
        var palette = new Rgba32[1 << h.BitCount];
        palette.AsSpan().Fill(Black);
        ReadOnlySpan<byte> src = file.Slice(h.PaletteOffset, h.PaletteCount * h.PaletteEntrySize);
        for (int i = 0; i < h.PaletteCount; i++)
        {
            ReadOnlySpan<byte> e = src.Slice(i * h.PaletteEntrySize, 3);
            palette[i] = new Rgba32(e[2], e[1], e[0], 255);
        }

        return palette;
    }

    // ------------------------------------------------------------------ uncompressed

    private static void DecodeUncompressed(ReadOnlySpan<byte> file, BmpHeader h, Rgba32[] pixels)
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
