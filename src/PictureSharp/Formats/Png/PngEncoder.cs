using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using PictureSharp.Compression;
using PictureSharp.Processing;
using PictureSharp.Formats.Metadata;
using PictureSharp.Internal;

namespace PictureSharp.Formats.Png;

/// <summary>Encoder for PNG and APNG files.</summary>
internal sealed partial class PngEncoder : IImageEncoder<PngEncoderOptions>
{
    public static PngEncoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Png;

    public void Encode(Image image, Stream output, PngEncoderOptions options, CancellationToken cancellationToken)
    {
        List<ImageFrame> frames = image.IsAnimated && options.SaveAnimation
            ? [.. image.Frames]
            : [image.Frames.RootFrame];

        Layout layout = ChooseLayout(frames, options.ColorType);
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
        ihdr[12] = (byte)(options.Interlace ? 1 : 0);
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
        if (MetadataSelection.Select(meta.IccProfile, options.Metadata, MetadataKinds.Icc) is { } icc)
        {
            byte[] name = "ICC Profile\0\0"u8.ToArray();
            byte[] compressed = Deflater.CompressZlib(icc, 9);
            writer.Write("iCCP", [.. name, .. compressed]);
        }

        if (image.Metadata.HorizontalResolution != 96f || image.Metadata.VerticalResolution != 96f)
        {
            Span<byte> phys = stackalloc byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)Math.Round(image.Metadata.HorizontalResolution / 0.0254));
            BinaryPrimitives.WriteUInt32BigEndian(phys[4..], (uint)Math.Round(image.Metadata.VerticalResolution / 0.0254));
            phys[8] = 1;  // unit: meter
            writer.Write("pHYs", phys);
        }

        if (MetadataSelection.Select(meta.Exif, options.Metadata, MetadataKinds.Exif) is { } exif)
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

            cancellationToken.ThrowIfCancellationRequested();
            byte[] raw = Serialize(frame, layout, options, cancellationToken);
            using Deflater deflater = Deflater.CompressZlibPooled(raw, options.CompressionLevel, options.MaxDegreeOfParallelism, cancellationToken);
            ReadOnlySpan<byte> compressed = deflater.Output;
            const int maxChunk = 1 << 20;
            for (int offset = 0; offset < compressed.Length || offset == 0; offset += maxChunk)
            {
                int len = Math.Min(maxChunk, compressed.Length - offset);
                if (index == 0)
                {
                    writer.Write("IDAT", compressed.Slice(offset, len));
                }
                else
                {
                    var fdat = new byte[len + 4];
                    BinaryPrimitives.WriteUInt32BigEndian(fdat, (uint)sequence++);
                    compressed.Slice(offset, len).CopyTo(fdat.AsSpan(4));
                    writer.Write("fdAT", fdat);
                }

                if (compressed.Length == 0)
                {
                    break;
                }
            }
        }

        if (MetadataSelection.Select(meta.Xmp, options.Metadata, MetadataKinds.Xmp) is { } xmp)
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

    // ------------------------------------------------------------------ scanlines

    private static byte[] Serialize(ImageFrame frame, Layout layout, PngEncoderOptions o, CancellationToken cancellationToken)
    {
        int width = frame.Width;
        int height = frame.Height;
        int bpp = PngConstants.Channels(layout.Color) * layout.BitDepth;

        // Palette and low bit depth images compress best unfiltered (libpng's recommendation).
        PngFilterStrategy strategy = o.FilterStrategy;
        if (strategy == PngFilterStrategy.Adaptive && (layout.Color == PngColor.Palette || layout.BitDepth < 8))
        {
            strategy = PngFilterStrategy.None;
        }

        int passCount = o.Interlace ? 7 : 1;
        long total = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            int pw = o.Interlace ? PngConstants.PassWidth(width, pass) : width;
            int ph = o.Interlace ? PngConstants.PassHeight(height, pass) : height;
            if (pw > 0 && ph > 0)
            {
                total += ph * (PngConstants.RowBytes(pw, bpp) + 1);
            }
        }

        byte[] output = GC.AllocateUninitializedArray<byte>(checked((int)total));
        int offset = 0;
        for (int pass = 0; pass < passCount; pass++)
        {
            var p = new Pass
            {
                Frame = frame, Layout = layout, Strategy = strategy, Bpp = bpp,
                X0 = o.Interlace ? PngConstants.Adam7XStart[pass] : 0,
                Y0 = o.Interlace ? PngConstants.Adam7YStart[pass] : 0,
                Dx = o.Interlace ? PngConstants.Adam7XStep[pass] : 1,
                Dy = o.Interlace ? PngConstants.Adam7YStep[pass] : 1,
                Width = o.Interlace ? PngConstants.PassWidth(width, pass) : width,
                Height = o.Interlace ? PngConstants.PassHeight(height, pass) : height,
            };
            if (p.Width > 0 && p.Height > 0)
            {
                offset += WritePass(p, output, offset, o.MaxDegreeOfParallelism, cancellationToken);
            }
        }

        return output;
    }

    private readonly record struct Pass
    {
        public required ImageFrame Frame { get; init; }

        public required Layout Layout { get; init; }

        public required PngFilterStrategy Strategy { get; init; }

        public required int Bpp { get; init; }

        public required int X0 { get; init; }

        public required int Y0 { get; init; }

        public required int Dx { get; init; }

        public required int Dy { get; init; }

        public required int Width { get; init; }

        public required int Height { get; init; }

        public int RowBytes => (int)PngConstants.RowBytes(Width, Bpp);
    }

    // Each band of rows packs the row above it again, so bands are independent and the output does not
    // depend on how rows are split.
    private static int WritePass(Pass pass, byte[] output, int offset, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        int stride = pass.RowBytes + 1;
        int size = pass.Height * stride;
        if (Parallelism.IsWorthwhile(maxDegreeOfParallelism, size, 1 << 18) && pass.Height > 1)
        {
            int degree = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
            int band = Math.Max(8, pass.Height / (4 * degree));
            int bands = (pass.Height + band - 1) / band;
            Parallel.For(0, bands, Parallelism.Options(maxDegreeOfParallelism, cancellationToken), b =>
            {
                int first = b * band;
                WriteRows(pass, output.AsSpan(offset, size), first, Math.Min(pass.Height, first + band), cancellationToken);
            });
        }
        else
        {
            WriteRows(pass, output.AsSpan(offset, size), 0, pass.Height, cancellationToken);
        }

        return size;
    }

    private static void WriteRows(Pass pass, Span<byte> output, int first, int end, CancellationToken cancellationToken)
    {
        int rowBytes = pass.RowBytes;
        int stride = rowBytes + 1;
        int pad = Math.Max(1, pass.Bpp / 8);
        int padded = pad + rowBytes;
        byte[] buffers = ArrayPool<byte>.Shared.Rent(2 * padded);
        Rgba32[]? gathered = pass.Dx != 1 || pass.X0 != 0 ? ArrayPool<Rgba32>.Shared.Rent(pass.Width) : null;
        try
        {
            Span<byte> prev = buffers.AsSpan(0, padded);
            Span<byte> cur = buffers.AsSpan(padded, padded);
            prev[..pad].Clear();
            cur[..pad].Clear();
            if (first == 0)
            {
                prev.Clear();
            }
            else
            {
                PackRow(pass, first - 1, gathered, prev[pad..]);
            }

            for (int j = first; j < end; j++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackRow(pass, j, gathered, cur[pad..]);
                int filter = pass.Strategy switch
                {
                    PngFilterStrategy.None => 0,
                    PngFilterStrategy.Adaptive => SelectFilter(cur, prev, pad),
                    _ => (int)pass.Strategy - 1,
                };
                Span<byte> row = output.Slice(j * stride, stride);
                row[0] = (byte)filter;
                ApplyFilter(filter, cur, prev, pad, row[1..]);
                Span<byte> swap = prev;
                prev = cur;
                cur = swap;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffers);
            if (gathered is not null)
            {
                ArrayPool<Rgba32>.Shared.Return(gathered);
            }
        }
    }

    private static void PackRow(Pass pass, int j, Rgba32[]? gathered, Span<byte> dst)
    {
        Span<Rgba32> source = pass.Frame.GetRowSpan(pass.Y0 + (j * pass.Dy));
        ReadOnlySpan<Rgba32> pixels;
        if (gathered is null)
        {
            pixels = source[..pass.Width];
        }
        else
        {
            for (int i = 0; i < pass.Width; i++)
            {
                gathered[i] = source[pass.X0 + (i * pass.Dx)];
            }

            pixels = gathered.AsSpan(0, pass.Width);
        }

        PackRow(pixels, pass.Layout, dst);
    }

    private static void PackRow(ReadOnlySpan<Rgba32> pixels, Layout layout, Span<byte> dst)
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
                if (depth < 8)
                {
                    dst.Clear();
                }

                int perByte = 8 / depth;
                uint lastColor = 0;
                byte lastIndex = 0;
                bool hasLast = false;
                for (int i = 0; i < pixels.Length; i++)
                {
                    // Neighbouring pixels usually repeat, so remember the last lookup.
                    uint color = pixels[i].PackedValue;
                    if (!hasLast || color != lastColor)
                    {
                        lastIndex = index[color];
                        lastColor = color;
                        hasLast = true;
                    }

                    if (depth == 8)
                    {
                        dst[i] = lastIndex;
                    }
                    else
                    {
                        int shift = 8 - (depth * ((i % perByte) + 1));
                        dst[i / perByte] |= (byte)(lastIndex << shift);
                    }
                }

                break;
            }
        }
    }
}
