using System.Buffers;

namespace RasterSharp.Formats.Pcx;

/// <summary>Decoder for ZSoft PCX images.</summary>
internal sealed class PcxDecoder : IImageDecoder
{
    public static PcxDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Pcx;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        PcxHeader header = PcxHeader.Parse(data.Span);
        if (!header.IsSupported)
        {
            throw Unsupported(header);
        }

        (float dpiX, float dpiY) = header.Resolution;
        return new ImageInfo
        {
            Format = ImageFormat.Pcx,
            Width = header.Width,
            Height = header.Height,
            HasAlpha = header.HasAlphaPlane,
            HorizontalResolution = dpiX,
            VerticalResolution = dpiY,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> file = data.Span;
        PcxHeader header = PcxHeader.Parse(file);
        options.ValidateDimensions(ImageFormat.Pcx, header.Width, header.Height);
        if (!header.IsSupported)
        {
            throw Unsupported(header);
        }

        long pixelCount = (long)header.Width * header.Height;
        if (pixelCount > Array.MaxLength)
        {
            throw new ImageFormatException(ImageFormat.Pcx, "The PCX image is too large.");
        }

        int scanBytes = header.Planes * header.BytesPerLine;
        // Even maximally compressed scanlines need this much input. Reject short files before
        // allocating pixels; a header alone must not force a multi-gigabyte allocation.
        int minimumRowBytes = scanBytes;
        if (header.Encoding == PcxConstants.EncodingRle)
        {
            int remainder = scanBytes % PcxConstants.MaxRun;
            minimumRowBytes = (scanBytes / PcxConstants.MaxRun * 2) + Math.Min(remainder, 2);
        }

        if ((long)minimumRowBytes * header.Height > file.Length - PcxConstants.HeaderSize)
        {
            throw new ImageFormatException(ImageFormat.Pcx, "The PCX image is truncated.");
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(scanBytes);
        Rgba32[] pixels;
        try
        {
            Span<byte> scanline = rented.AsSpan(0, scanBytes);
            bool vgaPalette = HasVgaPalette(file, header, scanline);
            ReadOnlySpan<byte> raster = vgaPalette ? file[..^PcxConstants.VgaTail] : file;
            Rgba32[] palette = CreatePalette(file, header, vgaPalette);
            pixels = GC.AllocateUninitializedArray<Rgba32>((int)pixelCount);
            int offset = PcxConstants.HeaderSize;
            for (int y = 0; y < header.Height; y++)
            {
                PcxRle.Decode(raster, ref offset, scanline, header.Encoding == PcxConstants.EncodingRle);
                Expand(header, scanline, pixels.AsSpan(y * header.Width, header.Width), palette);
            }

            if (!vgaPalette && header.Mode == 0x0108 && offset < file.Length && file[offset] == PcxConstants.VgaMarker)
            {
                throw new ImageFormatException(ImageFormat.Pcx, "The PCX color palette is truncated.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        (float dpiX, float dpiY) = header.Resolution;
        return new Image([new ImageFrame(header.Width, header.Height, pixels)])
        {
            SourceFormat = ImageFormat.Pcx,
            HasAlpha = header.HasAlphaPlane,
            Metadata = { HorizontalResolution = dpiX, VerticalResolution = dpiY },
        };
    }

    private static bool HasVgaPalette(ReadOnlySpan<byte> file, PcxHeader header, Span<byte> scanline)
    {
        if (header.Mode != 0x0108 || file.Length < PcxConstants.HeaderSize + PcxConstants.VgaTail
            || file[^PcxConstants.VgaTail] != PcxConstants.VgaMarker)
        {
            return false;
        }

        int paletteOffset = file.Length - PcxConstants.VgaTail;
        if (header.Encoding == PcxConstants.EncodingNone)
        {
            return (long)scanline.Length * header.Height <= paletteOffset - PcxConstants.HeaderSize;
        }

        // The apparent marker can be a grayscale sample. Locate the raster's end before reserving
        // a palette trailer, using the pooled scanline without allocating image pixels.
        int offset = PcxConstants.HeaderSize;
        for (int y = 0; y < header.Height; y++)
        {
            PcxRle.Decode(file, ref offset, scanline, compressed: true);
            if (offset > paletteOffset)
            {
                return false;
            }
        }

        return true;
    }

    private static Rgba32[] CreatePalette(ReadOnlySpan<byte> file, PcxHeader header, bool vgaPalette)
    {
        if (header.Mode == 0x0108)
        {
            var palette = new Rgba32[256];
            if (vgaPalette)
            {
                ReadRgb(file[(file.Length - 768)..], palette);
            }
            else
            {
                for (int i = 0; i < palette.Length; i++)
                {
                    palette[i] = new Rgba32((byte)i, (byte)i, (byte)i);
                }
            }

            return palette;
        }

        if (header.BitsPerPixel == 8)
        {
            return [];
        }

        var ega = new Rgba32[16];
        if (!header.HasHeaderPalette)
        {
            ega[0] = Rgba32.Black;
            ega[1] = Rgba32.White;
            return ega;
        }

        ReadRgb(file.Slice(16, 48), ega);
        if (header.Mode == 0x0101 && ega[0] == new Rgba32(0, 0, 0) && ega[1] == new Rgba32(0, 0, 0))
        {
            ega[1] = Rgba32.White;
        }

        return ega;
    }

    private static void ReadRgb(ReadOnlySpan<byte> rgb, Span<Rgba32> palette)
    {
        for (int i = 0; i < palette.Length; i++)
        {
            int o = i * 3;
            palette[i] = new Rgba32(rgb[o], rgb[o + 1], rgb[o + 2]);
        }
    }

    private static void Expand(PcxHeader header, ReadOnlySpan<byte> scanline, Span<Rgba32> dest, Rgba32[] palette)
    {
        int stride = header.BytesPerLine;
        switch (header.Mode)
        {
            case 0x0308:
                ExpandChannels(scanline, stride, dest, alpha: false);
                break;
            case 0x0408:
                ExpandChannels(scanline, stride, dest, alpha: true);
                break;
            case 0x0108:
            case 0x0102:
            case 0x0104:
                ExpandPacked(header.BitsPerPixel, scanline, dest, palette);
                break;
            default:
                ExpandPlanar(header.Planes, stride, scanline, dest, palette);
                break;
        }
    }

    private static void ExpandChannels(ReadOnlySpan<byte> scanline, int stride, Span<Rgba32> dest, bool alpha)
    {
        ReadOnlySpan<byte> red = scanline;
        ReadOnlySpan<byte> green = scanline[stride..];
        ReadOnlySpan<byte> blue = scanline[(stride * 2)..];
        ReadOnlySpan<byte> alphaPlane = alpha ? scanline[(stride * 3)..] : default;
        for (int x = 0; x < dest.Length; x++)
        {
            dest[x] = new Rgba32(red[x], green[x], blue[x], alpha ? alphaPlane[x] : (byte)255);
        }
    }

    private static void ExpandPacked(int bits, ReadOnlySpan<byte> scanline, Span<Rgba32> dest, Rgba32[] palette)
    {
        int mask = (1 << bits) - 1;
        for (int x = 0; x < dest.Length; x++)
        {
            int bit = x * bits;
            int index = (scanline[bit >> 3] >> (8 - bits - (bit & 7))) & mask;
            dest[x] = palette[index];
        }
    }

    private static void ExpandPlanar(int planes, int stride, ReadOnlySpan<byte> scanline, Span<Rgba32> dest, Rgba32[] palette)
    {
        for (int x = 0; x < dest.Length; x++)
        {
            int mask = 0x80 >> (x & 7);
            int index = 0;
            int sample = x >> 3;
            for (int plane = 0; plane < planes; plane++)
            {
                if ((scanline[(plane * stride) + sample] & mask) != 0)
                {
                    index |= 1 << plane;
                }
            }

            dest[x] = palette[index];
        }
    }

    private static NotSupportedException Unsupported(PcxHeader header) =>
        new($"Decoding version {header.Version} PCX images with {header.BitsPerPixel} bits per plane and {header.Planes} planes is not supported.");
}
