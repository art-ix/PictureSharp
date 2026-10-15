using System.Buffers;
using System.Buffers.Binary;
using PictureSharp.Application.Abstractions;
using PictureSharp.Formats.Pcx;

namespace PictureSharp.Infrastructure.Codecs.Pcx;

/// <summary>Encoder for ZSoft PCX images. Only the first frame is written.</summary>
internal sealed class PcxEncoder : IImageEncoder<PcxEncoderOptions>
{
    public static PcxEncoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Pcx;

    public void Encode(Image image, Stream output, PcxEncoderOptions options, CancellationToken cancellationToken)
    {
        ImageFrame frame = image.Frames.RootFrame;
        int width = frame.Width;
        int height = frame.Height;
        if (width is < 1 or > 65536 || height is < 1 or > 65536)
        {
            throw new NotSupportedException("The image is too large for the PCX format.");
        }

        ReadOnlySpan<Rgba32> pixels = frame.GetPixelSpan();
        Layout layout = ChooseLayout(pixels, options.BitsPerPixel);
        int bytesPerLine = PlaneStride(width, layout.Bits, layout.Planes);
        int scanBytes = layout.Planes * bytesPerLine;

        var header = new byte[PcxConstants.HeaderSize];
        header[0] = PcxConstants.Manufacturer;
        header[1] = (byte)layout.Version;
        header[2] = PcxConstants.EncodingRle;
        header[3] = (byte)layout.Bits;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(height - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), Dpi(image.Metadata.HorizontalResolution));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), Dpi(image.Metadata.VerticalResolution));
        if (layout.Palette is not null)
        {
            int count = Math.Min(layout.Palette.Length, 16);
            for (int i = 0; i < count; i++)
            {
                header[16 + (i * 3)] = layout.Palette[i].R;
                header[17 + (i * 3)] = layout.Palette[i].G;
                header[18 + (i * 3)] = layout.Palette[i].B;
            }
        }

        header[65] = (byte)layout.Planes;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)bytesPerLine);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68), (ushort)(layout.GrayRamp ? 2 : 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(70), (ushort)Math.Min(width, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(72), (ushort)Math.Min(height, ushort.MaxValue));
        output.Write(header);

        Dictionary<uint, byte>? lookup = layout.Palette is null || layout.BlackWhite ? null : Lookup(layout.Palette);
        byte[] scan = ArrayPool<byte>.Shared.Rent(scanBytes);
        byte[]? encoded = null;
        try
        {
            encoded = ArrayPool<byte>.Shared.Rent(scanBytes * 2);
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Span<byte> row = scan.AsSpan(0, scanBytes);
                row.Clear();
                Pack(pixels.Slice(y * width, width), row, bytesPerLine, layout, lookup);
                int count = PcxRle.Encode(row, encoded);
                output.Write(encoded.AsSpan(0, count));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scan);
            if (encoded is not null)
            {
                ArrayPool<byte>.Shared.Return(encoded);
            }
        }

        if (layout.Bits == 8 && layout.Planes == 1)
        {
            output.WriteByte(PcxConstants.VgaMarker);
            output.Write(VgaPalette(layout));
        }
    }

    private readonly struct Layout
    {
        public int Version { get; init; }
        public int Bits { get; init; }
        public int Planes { get; init; }
        public Rgba32[]? Palette { get; init; }
        public bool GrayRamp { get; init; }
        public bool BlackWhite { get; init; }
    }

    private static Layout ChooseLayout(ReadOnlySpan<Rgba32> pixels, int requested)
    {
        if (requested == 32)
        {
            return new Layout { Version = 5, Bits = 8, Planes = 4 };
        }

        if (requested == 24)
        {
            return new Layout { Version = 5, Bits = 8, Planes = 3 };
        }

        int limit = requested switch { 1 => 2, 4 => 16, 8 => 256, _ => 256 };
        var colors = new List<Rgba32>();
        var seen = new HashSet<uint>();
        bool gray = true;
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 pixel = pixels[i];
            if (requested == 0 && pixel.A != 255)
            {
                return new Layout { Version = 5, Bits = 8, Planes = 4 };
            }

            gray &= pixel.R == pixel.G && pixel.G == pixel.B;
            var opaque = new Rgba32(pixel.R, pixel.G, pixel.B);
            if (seen.Add(opaque.PackedValue))
            {
                colors.Add(opaque);
                if (requested != 0 && colors.Count > limit)
                {
                    throw new InvalidOperationException(
                        $"The image has more than {limit} colors and cannot be saved with {requested} bits per pixel.");
                }

                if (requested == 0 && colors.Count > 256)
                {
                    for (int j = i + 1; j < pixels.Length; j++)
                    {
                        if (pixels[j].A != 255)
                        {
                            return new Layout { Version = 5, Bits = 8, Planes = 4 };
                        }
                    }

                    return new Layout { Version = 5, Bits = 8, Planes = 3 };
                }
            }
        }

        return requested switch
        {
            1 => Mono(colors),
            4 => Planar(colors, 4),
            8 => Indexed(colors, gray),
            _ => colors.Count switch
            {
                <= 2 => Mono(colors),
                <= 4 => Planar(colors, 2),
                <= 16 => Planar(colors, 4),
                _ => Indexed(colors, gray),
            },
        };
    }

    private static Layout Mono(List<Rgba32> colors)
    {
        bool blackWhite = colors.All(static color => color == Rgba32.Black || color == Rgba32.White);
        return new Layout
        {
            Version = 2,
            Bits = 1,
            Planes = 1,
            Palette = blackWhite ? [Rgba32.Black, Rgba32.White] : [.. colors],
            BlackWhite = blackWhite,
        };
    }

    private static Layout Planar(List<Rgba32> colors, int planes) => new()
    {
        Version = 5,
        Bits = 1,
        Planes = planes,
        Palette = [.. colors],
    };

    private static Layout Indexed(List<Rgba32> colors, bool gray) => new()
    {
        Version = 5,
        Bits = 8,
        Planes = 1,
        Palette = gray ? null : [.. colors],
        GrayRamp = gray,
    };

    private static Dictionary<uint, byte> Lookup(Rgba32[] palette)
    {
        var lookup = new Dictionary<uint, byte>(palette.Length);
        for (int i = 0; i < palette.Length; i++)
        {
            lookup[palette[i].PackedValue] = (byte)i;
        }

        return lookup;
    }

    private static void Pack(ReadOnlySpan<Rgba32> source, Span<byte> row, int bytesPerLine, Layout layout, Dictionary<uint, byte>? lookup)
    {
        if (layout.Bits == 8 && layout.Planes >= 3)
        {
            for (int x = 0; x < source.Length; x++)
            {
                Rgba32 pixel = source[x];
                row[x] = pixel.R;
                row[bytesPerLine + x] = pixel.G;
                row[(bytesPerLine * 2) + x] = pixel.B;
                if (layout.Planes == 4)
                {
                    row[(bytesPerLine * 3) + x] = pixel.A;
                }
            }

            return;
        }

        if (layout.GrayRamp)
        {
            for (int x = 0; x < source.Length; x++)
            {
                row[x] = source[x].R;
            }

            return;
        }

        if (layout.Bits == 8)
        {
            for (int x = 0; x < source.Length; x++)
            {
                row[x] = lookup![Opaque(source[x]).PackedValue];
            }

            return;
        }

        for (int x = 0; x < source.Length; x++)
        {
            int index = layout.BlackWhite
                ? source[x].R == 255 && source[x].G == 255 && source[x].B == 255 ? 1 : 0
                : lookup![Opaque(source[x]).PackedValue];
            int mask = 0x80 >> (x & 7);
            int sample = x >> 3;
            for (int plane = 0; plane < layout.Planes; plane++)
            {
                if ((index & (1 << plane)) != 0)
                {
                    row[(plane * bytesPerLine) + sample] |= (byte)mask;
                }
            }
        }
    }

    private static Rgba32 Opaque(Rgba32 pixel) => new(pixel.R, pixel.G, pixel.B);

    private static byte[] VgaPalette(Layout layout)
    {
        var palette = new byte[768];
        if (layout.GrayRamp)
        {
            for (int i = 0; i < 256; i++)
            {
                palette[i * 3] = palette[(i * 3) + 1] = palette[(i * 3) + 2] = (byte)i;
            }
        }
        else
        {
            Rgba32[] colors = layout.Palette!;
            for (int i = 0; i < colors.Length; i++)
            {
                palette[i * 3] = colors[i].R;
                palette[(i * 3) + 1] = colors[i].G;
                palette[(i * 3) + 2] = colors[i].B;
            }
        }

        return palette;
    }

    // 1-bit images with several planes keep the raw byte count. ZSoft asks for an even stride, but
    // Pillow 10.2 spaces those planes by (width + 7) / 8 and treats the padding byte as the next plane.
    // ImageMagick reads both. One plane and every 8-bit layout stay even; Pillow strips that padding.
    private static int PlaneStride(int width, int bits, int planes)
    {
        int raw = (int)(((long)width * bits + 7) / 8);
        int stride = bits == 1 && planes > 1 ? raw : raw + (raw & 1);
        if ((uint)stride > ushort.MaxValue)
        {
            throw new NotSupportedException("The image is too large for the PCX format.");
        }

        return stride;
    }

    private static ushort Dpi(float dpi)
    {
        if (!float.IsFinite(dpi) || dpi < 1f || dpi > 65535f)
        {
            return 96;
        }

        int rounded = (int)Math.Round(dpi, MidpointRounding.AwayFromZero);
        return rounded is >= 1 and <= 65535 ? (ushort)rounded : (ushort)96;
    }
}
