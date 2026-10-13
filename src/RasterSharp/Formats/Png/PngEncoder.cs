using System.Buffers.Binary;
using System.Text;
using RasterSharp.Compression;
using RasterSharp.Processing;

namespace RasterSharp.Formats.Png;

/// <summary>Encoder for PNG and APNG files.</summary>
internal sealed class PngEncoder : IImageEncoder
{
    public static PngEncoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Png;

    public void Encode(Image image, Stream output, EncoderOptions options)
    {
        var o = (PngEncoderOptions)options;
        List<ImageFrame> frames = image.IsAnimated && o.SaveAnimation
            ? [.. image.Frames]
            : [image.ActiveFrame];

        Layout layout = ChooseLayout(frames, o.ColorType);
        int width = image.Width;
        int height = image.Height;

        var writer = new ChunkWriter(output);
        output.Write(PngConstants.Signature);

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..], (uint)height);
        ihdr[8] = (byte)layout.BitDepth;
        ihdr[9] = (byte)layout.Color;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = (byte)(o.Interlace ? 1 : 0);
        writer.Write("IHDR", ihdr);

        bool animated = frames.Count > 1;
        if (animated)
        {
            Span<byte> actl = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Count);
            BinaryPrimitives.WriteUInt32BigEndian(actl[4..], (uint)image.LoopCount);
            writer.Write("acTL", actl);
        }

        ImageMetadata meta = image.Metadata;
        if ((o.Metadata & MetadataKinds.Icc) != 0 && meta.IccProfile is { Length: > 0 } icc)
        {
            byte[] name = "ICC Profile\0\0"u8.ToArray();
            byte[] compressed = Deflater.CompressZlib(icc, 9);
            writer.Write("iCCP", [.. name, .. compressed]);
        }

        if (image.HorizontalResolution != 96f || image.VerticalResolution != 96f)
        {
            Span<byte> phys = stackalloc byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)Math.Round(image.HorizontalResolution / 0.0254));
            BinaryPrimitives.WriteUInt32BigEndian(phys[4..], (uint)Math.Round(image.VerticalResolution / 0.0254));
            phys[8] = 1;  // unit: meter
            writer.Write("pHYs", phys);
        }

        if ((o.Metadata & MetadataKinds.Exif) != 0 && meta.Exif is { Length: > 0 } exif)
        {
            writer.Write("eXIf", exif);
        }

        if (layout.Palette is { } palette)
        {
            var plte = new byte[palette.Length * 3];
            int lastTransparent = -1;
            for (int i = 0; i < palette.Length; i++)
            {
                plte[3 * i] = palette[i].R;
                plte[(3 * i) + 1] = palette[i].G;
                plte[(3 * i) + 2] = palette[i].B;
                if (palette[i].A != 255)
                {
                    lastTransparent = i;
                }
            }

            writer.Write("PLTE", plte);
            if (lastTransparent >= 0)
            {
                var trns = new byte[lastTransparent + 1];
                for (int i = 0; i <= lastTransparent; i++)
                {
                    trns[i] = palette[i].A;
                }

                writer.Write("tRNS", trns);
            }
        }

        int sequence = 0;
        Span<byte> fctl = stackalloc byte[26];
        for (int index = 0; index < frames.Count; index++)
        {
            ImageFrame frame = frames[index];
            if (animated)
            {
                BinaryPrimitives.WriteUInt32BigEndian(fctl, (uint)sequence++);
                BinaryPrimitives.WriteUInt32BigEndian(fctl[4..], (uint)width);
                BinaryPrimitives.WriteUInt32BigEndian(fctl[8..], (uint)height);
                BinaryPrimitives.WriteUInt32BigEndian(fctl[12..], 0);
                BinaryPrimitives.WriteUInt32BigEndian(fctl[16..], 0);
                (ushort num, ushort den) = ToDelay(frame.Duration);
                BinaryPrimitives.WriteUInt16BigEndian(fctl[20..], num);
                BinaryPrimitives.WriteUInt16BigEndian(fctl[22..], den);
                fctl[24] = 0;  // dispose: none
                fctl[25] = 0;  // blend: source
                writer.Write("fcTL", fctl);
            }

            byte[] raw = Serialize(frame, layout, o);
            byte[] compressed = Deflater.CompressZlib(raw, o.CompressionLevel);
            const int maxChunk = 1 << 20;
            for (int offset = 0; offset < compressed.Length || offset == 0; offset += maxChunk)
            {
                int len = Math.Min(maxChunk, compressed.Length - offset);
                if (index == 0)
                {
                    writer.Write("IDAT", compressed.AsSpan(offset, len));
                }
                else
                {
                    var fdat = new byte[len + 4];
                    BinaryPrimitives.WriteUInt32BigEndian(fdat, (uint)sequence++);
                    compressed.AsSpan(offset, len).CopyTo(fdat.AsSpan(4));
                    writer.Write("fdAT", fdat);
                }

                if (compressed.Length == 0)
                {
                    break;
                }
            }
        }

        if ((o.Metadata & MetadataKinds.Xmp) != 0 && meta.Xmp is { Length: > 0 } xmp)
        {
            // iTXt: keyword, NUL, compression flag, compression method, language tag NUL, translated keyword NUL, text
            byte[] header = [.. Encoding.ASCII.GetBytes(PngConstants.XmpKeyword), 0, 0, 0, 0, 0];
            writer.Write("iTXt", [.. header, .. xmp]);
        }

        writer.Write("IEND", []);
    }

    private static (ushort Num, ushort Den) ToDelay(TimeSpan duration)
    {
        double ms = Math.Max(0, duration.TotalMilliseconds);
        if (ms <= ushort.MaxValue && Math.Abs(ms - Math.Round(ms)) < 1e-9)
        {
            return ((ushort)Math.Round(ms), 1000);
        }

        // Fall back to hundredths of a second for long or fractional delays.
        return ((ushort)Math.Min(ushort.MaxValue, Math.Round(ms / 10)), 100);
    }

    // ------------------------------------------------------------------ color type selection

    private sealed class Layout
    {
        public PngColor Color;
        public int BitDepth = 8;
        public Rgba32[]? Palette;
        public Dictionary<uint, byte>? PaletteIndex;
    }

    private static Layout ChooseLayout(List<ImageFrame> frames, PngColorType requested)
    {
        bool hasAlpha = false;
        bool isGray = true;
        var colors = new Dictionary<uint, byte>();
        bool fitsPalette = true;
        foreach (ImageFrame frame in frames)
        {
            foreach (Rgba32 p in frame.GetPixelSpan())
            {
                hasAlpha |= p.A != 255;
                isGray &= p.R == p.G && p.G == p.B;
                if (fitsPalette && !colors.ContainsKey(p.PackedValue))
                {
                    if (colors.Count == 256)
                    {
                        fitsPalette = false;
                    }
                    else
                    {
                        colors.Add(p.PackedValue, 0);
                    }
                }
            }
        }

        PngColorType type = requested;
        if (type == PngColorType.Auto)
        {
            type = isGray && !hasAlpha ? PngColorType.Grayscale
                : fitsPalette ? PngColorType.Palette
                : isGray ? PngColorType.GrayscaleAlpha
                : hasAlpha ? PngColorType.Rgba : PngColorType.Rgb;
        }

        switch (type)
        {
            case PngColorType.Grayscale:
                return new Layout { Color = PngColor.Gray };
            case PngColorType.GrayscaleAlpha:
                return new Layout { Color = PngColor.GrayAlpha };
            case PngColorType.Rgb:
                return new Layout { Color = PngColor.Rgb };
            case PngColorType.Rgba:
                return new Layout { Color = PngColor.Rgba };
            default:
            {
                if (!fitsPalette)
                {
                    throw new InvalidOperationException("The image has more than 256 colors and cannot be saved as an indexed PNG.");
                }

                // Transparent entries first so the tRNS chunk stays short; then by frequency-agnostic value order.
                Rgba32[] palette = colors.Keys.Select(k => new Rgba32 { PackedValue = k })
                    .OrderBy(c => c.A == 255 ? 1 : 0).ThenBy(c => c.PackedValue).ToArray();
                var index = new Dictionary<uint, byte>(palette.Length);
                for (int i = 0; i < palette.Length; i++)
                {
                    index[palette[i].PackedValue] = (byte)i;
                }

                int depth = palette.Length <= 2 ? 1 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 4 : 8;
                return new Layout { Color = PngColor.Palette, BitDepth = depth, Palette = palette, PaletteIndex = index };
            }
        }
    }

    // ------------------------------------------------------------------ scanlines

    private static byte[] Serialize(ImageFrame frame, Layout layout, PngEncoderOptions o)
    {
        int width = frame.Width;
        int height = frame.Height;
        int bpp = PngConstants.Channels(layout.Color) * layout.BitDepth;
        int filterBpp = Math.Max(1, bpp / 8);

        // Palette and low bit depth images compress best unfiltered (libpng's recommendation).
        PngFilterStrategy strategy = o.FilterStrategy;
        if (strategy == PngFilterStrategy.Adaptive && (layout.Color == PngColor.Palette || layout.BitDepth < 8))
        {
            strategy = PngFilterStrategy.None;
        }

        using var ms = new MemoryStream();
        if (!o.Interlace)
        {
            WritePass(frame, layout, 0, 0, 1, 1, width, height, bpp, filterBpp, strategy, ms);
        }
        else
        {
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = PngConstants.PassWidth(width, pass);
                int ph = PngConstants.PassHeight(height, pass);
                if (pw > 0 && ph > 0)
                {
                    WritePass(frame, layout, PngConstants.Adam7XStart[pass], PngConstants.Adam7YStart[pass],
                        PngConstants.Adam7XStep[pass], PngConstants.Adam7YStep[pass], pw, ph, bpp, filterBpp, strategy, ms);
                }
            }
        }

        return ms.ToArray();
    }

    private static void WritePass(ImageFrame frame, Layout layout, int x0, int y0, int dx, int dy, int pw, int ph,
        int bpp, int filterBpp, PngFilterStrategy strategy, MemoryStream output)
    {
        int rowBytes = (int)PngConstants.RowBytes(pw, bpp);
        var prev = new byte[rowBytes];
        var cur = new byte[rowBytes];
        var pixels = new Rgba32[pw];
        var filtered = new byte[5][];
        for (int f = 0; f < 5; f++)
        {
            filtered[f] = new byte[rowBytes];
        }

        for (int j = 0; j < ph; j++)
        {
            Span<Rgba32> row = frame.GetRowSpan(y0 + (j * dy));
            for (int i = 0; i < pw; i++)
            {
                pixels[i] = row[x0 + (i * dx)];
            }

            PackRow(pixels, layout, cur);
            bool first = j == 0;
            int best;
            switch (strategy)
            {
                case PngFilterStrategy.None:
                    best = 0;
                    cur.CopyTo(filtered[0], 0);
                    break;
                case PngFilterStrategy.Sub:
                case PngFilterStrategy.Up:
                case PngFilterStrategy.Average:
                case PngFilterStrategy.Paeth:
                    best = (int)strategy - 1;
                    ApplyFilter(best, cur, first ? null : prev, filterBpp, filtered[best]);
                    break;
                default:
                {
                    best = 0;
                    long bestSum = long.MaxValue;
                    for (int f = 0; f < 5; f++)
                    {
                        ApplyFilter(f, cur, first ? null : prev, filterBpp, filtered[f]);
                        long sum = 0;
                        foreach (byte b in filtered[f])
                        {
                            sum += b < 128 ? b : 256 - b;
                        }

                        if (sum < bestSum)
                        {
                            bestSum = sum;
                            best = f;
                        }
                    }

                    break;
                }
            }

            output.WriteByte((byte)best);
            output.Write(filtered[best]);
            (prev, cur) = (cur, prev);
        }
    }

    private static void ApplyFilter(int filter, byte[] row, byte[]? prev, int bpp, byte[] dst)
    {
        int n = row.Length;
        switch (filter)
        {
            case 0:
                row.CopyTo(dst, 0);
                break;
            case 1:
                for (int i = 0; i < n; i++)
                {
                    dst[i] = (byte)(row[i] - (i >= bpp ? row[i - bpp] : 0));
                }

                break;
            case 2:
                for (int i = 0; i < n; i++)
                {
                    dst[i] = (byte)(row[i] - (prev is null ? 0 : prev[i]));
                }

                break;
            case 3:
                for (int i = 0; i < n; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = prev is null ? 0 : prev[i];
                    dst[i] = (byte)(row[i] - ((left + up) >> 1));
                }

                break;
            default:
                for (int i = 0; i < n; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = prev is null ? 0 : prev[i];
                    int upLeft = i >= bpp && prev is not null ? prev[i - bpp] : 0;
                    dst[i] = (byte)(row[i] - PngConstants.Paeth(left, up, upLeft));
                }

                break;
        }
    }

    private static void PackRow(ReadOnlySpan<Rgba32> pixels, Layout layout, byte[] dst)
    {
        switch (layout.Color)
        {
            case PngColor.Gray:
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba32 p = pixels[i];
                    dst[i] = p.R == p.G && p.G == p.B ? p.R : PixelConverter.Luma(p.R, p.G, p.B);
                }

                break;
            case PngColor.GrayAlpha:
                for (int i = 0; i < pixels.Length; i++)
                {
                    Rgba32 p = pixels[i];
                    dst[2 * i] = p.R == p.G && p.G == p.B ? p.R : PixelConverter.Luma(p.R, p.G, p.B);
                    dst[(2 * i) + 1] = p.A;
                }

                break;
            case PngColor.Rgb:
                PixelConverter.FromRgba(pixels, dst, PixelFormat.Rgb24);
                break;
            case PngColor.Rgba:
                PixelConverter.FromRgba(pixels, dst, PixelFormat.Rgba32);
                break;
            default:
            {
                Dictionary<uint, byte> index = layout.PaletteIndex!;
                int depth = layout.BitDepth;
                if (depth == 8)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        dst[i] = index[pixels[i].PackedValue];
                    }
                }
                else
                {
                    Array.Clear(dst);
                    int perByte = 8 / depth;
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        int shift = 8 - (depth * ((i % perByte) + 1));
                        dst[i / perByte] |= (byte)(index[pixels[i].PackedValue] << shift);
                    }
                }

                break;
            }
        }
    }

    private sealed class ChunkWriter(Stream output)
    {
        public void Write(string type, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, header[4..]);
            output.Write(header);
            output.Write(data);
            uint crc = Crc32.Update(Crc32.Compute(header[4..]), data);
            Span<byte> trailer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
            output.Write(trailer);
        }
    }
}
