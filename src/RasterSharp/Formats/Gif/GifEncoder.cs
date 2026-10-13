using System.Buffers.Binary;

namespace RasterSharp.Formats.Gif;

/// <summary>
/// Encoder for GIF89a (GIF87a when no extension is needed). Animation frames are written as the rectangles that
/// changed, with unchanged pixels transparent where the palette has a transparent entry; a frame is disposed to the
/// background only when the next frame makes pixels transparent.
/// </summary>
internal sealed class GifEncoder : IImageEncoder<GifEncoderOptions>
{
    public static GifEncoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Gif;

    /// <summary>A frame waiting to be written: its disposal and rectangle may still change when the next frame is seen.</summary>
    private sealed class PendingFrame
    {
        /// <summary>The frame's palette; null for local palettes, which are built when the rectangle is final.</summary>
        public GifFramePalette? Palette { get; set; }

        /// <summary>The canvas after this frame is drawn.</summary>
        public required Rgba32[] Rendered { get; init; }

        /// <summary>The canvas before this frame is drawn (after the previous frame's disposal).</summary>
        public required Rgba32[] Before { get; init; }

        public bool IsFirst { get; init; }

        public int X0;
        public int Y0;
        public int X1;
        public int Y1;
        public GifDisposal Disposal;
        public double DurationMs;
    }

    public void Encode(Image image, Stream output, GifEncoderOptions options)
    {
        int width = image.Width;
        int height = image.Height;
        if (width > ushort.MaxValue || height > ushort.MaxValue)
        {
            throw new NotSupportedException($"GIF images are limited to {ushort.MaxValue} pixels per side.");
        }

        IReadOnlyList<ImageFrame> frames = options.SaveAnimation ? [.. image.Frames] : [image.Frames.RootFrame];

        // Exact local palettes are tried first; a frame whose drawn pixels need too many colors switches the whole
        // animation to one shared quantized palette.
        byte[] encoded = TryEncode(image, frames, options, new GifPaletteBuilder(frames, options))
            ?? TryEncode(image, frames, options, new GifPaletteBuilder(frames, options, forceShared: true))
            ?? throw new InvalidOperationException("GIF palette selection failed.");
        output.Write(encoded);
    }

    private static byte[]? TryEncode(Image image, IReadOnlyList<ImageFrame> frames, GifEncoderOptions options, GifPaletteBuilder palettes)
    {
        int width = image.Width;
        int height = image.Height;
        bool animated = frames.Count > 1;
        using var stream = new MemoryStream();

        GifFramePalette first;
        Rgba32[] firstRendered;
        if (palettes.IsLocal)
        {
            firstRendered = palettes.Binarize(0);
            GifFramePalette? local = palettes.BuildLocal(firstRendered, new Rgba32[width * height], width, (0, 0, width, height), skipUnchanged: false);
            if (local is null)
            {
                return null;
            }

            first = local;
        }
        else
        {
            first = palettes.Build(0);
            firstRendered = GifPaletteBuilder.Render(first);
        }

        bool hasMetadata = Icc(image, options) is not null || Xmp(image, options) is not null;
        bool extended = animated || first.TransparentIndex >= 0 || hasMetadata;
        WriteHeader(stream, image, width, height, first, extended);
        if (animated && image.LoopCount != 1)
        {
            // NETSCAPE2.0 stores the number of repetitions after the first play (0 = forever).
            int repetitions = image.LoopCount == 0 ? 0 : Math.Min(image.LoopCount - 1, ushort.MaxValue);
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
            stream.Write(GifConstants.Netscape);
            stream.Write([3, 1, (byte)repetitions, (byte)(repetitions >> 8), 0]);
        }

        WriteMetadata(stream, image, options);

        var pending = new PendingFrame
        {
            Palette = first,
            Rendered = firstRendered,
            Before = new Rgba32[width * height],
            IsFirst = true,
            X1 = width,
            Y1 = height,
            DurationMs = frames[0].Duration.TotalMilliseconds,
        };

        for (int index = 1; index < frames.Count; index++)
        {
            GifFramePalette? palette = palettes.IsLocal ? null : palettes.Build(index);
            Rgba32[] rendered = palette is null ? palettes.Binarize(index) : GifPaletteBuilder.Render(palette);
            Rgba32[] before = Dispose(pending, rendered, width, height, options.OptimizeFrames);

            (int x0, int y0, int x1, int y1) = (0, 0, width, height);
            if (options.OptimizeFrames)
            {
                (x0, y0, x1, y1) = ChangedRectangle(before, rendered, width, height);
                if (x1 <= x0)
                {
                    if (pending.Disposal == GifDisposal.None)
                    {
                        // Identical to the previous frame: show that one longer.
                        pending.DurationMs += frames[index].Duration.TotalMilliseconds;
                        continue;
                    }

                    // Only the disposal changed the canvas; a one-pixel frame carries the timing.
                    (x0, y0, x1, y1) = (pending.X0, pending.Y0, pending.X0 + 1, pending.Y0 + 1);
                }
            }

            if (!WriteFrame(stream, pending, palettes, width, animated, options))
            {
                return null;
            }

            pending = new PendingFrame
            {
                Palette = palette,
                Rendered = rendered,
                Before = before,
                X0 = x0,
                Y0 = y0,
                X1 = x1,
                Y1 = y1,
                DurationMs = frames[index].Duration.TotalMilliseconds,
            };
        }

        if (!WriteFrame(stream, pending, palettes, width, animated, options))
        {
            return null;
        }

        stream.WriteByte(GifConstants.Trailer);
        return stream.ToArray();
    }

    /// <summary>
    /// Chooses the disposal of the pending frame and returns the canvas the next frame is drawn on. Pixels that
    /// become transparent can only be produced by "restore to background", so the pending frame's rectangle grows
    /// to cover them.
    /// </summary>
    private static Rgba32[] Dispose(PendingFrame pending, Rgba32[] next, int width, int height, bool optimize)
    {
        Rgba32[] current = pending.Rendered;
        (int x0, int y0, int x1, int y1) = (width, height, 0, 0);
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (current[row + x].A != 0 && next[row + x].A == 0)
                {
                    x0 = Math.Min(x0, x);
                    x1 = Math.Max(x1, x + 1);
                    y0 = Math.Min(y0, y);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        var before = (Rgba32[])current.Clone();
        if (x1 <= x0)
        {
            pending.Disposal = GifDisposal.None;
            return before;
        }

        pending.Disposal = GifDisposal.Background;
        if (optimize)
        {
            pending.X0 = Math.Min(pending.X0, x0);
            pending.Y0 = Math.Min(pending.Y0, y0);
            pending.X1 = Math.Max(pending.X1, x1);
            pending.Y1 = Math.Max(pending.Y1, y1);
        }

        for (int y = pending.Y0; y < pending.Y1; y++)
        {
            before.AsSpan((y * width) + pending.X0, pending.X1 - pending.X0).Clear();
        }

        return before;
    }

    private static (int X0, int Y0, int X1, int Y1) ChangedRectangle(Rgba32[] before, Rgba32[] after, int width, int height)
    {
        (int x0, int y0, int x1, int y1) = (width, height, 0, 0);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba32> a = before.AsSpan(y * width, width);
            ReadOnlySpan<Rgba32> b = after.AsSpan(y * width, width);
            int first = a.CommonPrefixLength(b);
            if (first == width)
            {
                continue;
            }

            int last = width - 1;
            while (a[last] == b[last])
            {
                last--;
            }

            x0 = Math.Min(x0, first);
            x1 = Math.Max(x1, last + 1);
            y0 = Math.Min(y0, y);
            y1 = y + 1;
        }

        return (x0, y0, x1, y1);
    }

    private static void WriteHeader(Stream stream, Image image, int width, int height, GifFramePalette first, bool extended)
    {
        int bits = TableBits(first.Colors.Length);
        Span<byte> header = stackalloc byte[13];
        (extended ? "GIF89a"u8 : "GIF87a"u8).CopyTo(header);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], (ushort)height);
        header[10] = (byte)(0x80 | ((bits - 1) << 4) | (bits - 1));
        header[11] = (byte)BackgroundIndex(image.AnimationBackgroundColor, first);
        header[12] = 0;
        stream.Write(header);
        WriteColorTable(stream, first.Colors, bits);
    }

    /// <summary>The global table entry matching the background color; the transparent entry for a transparent background.</summary>
    private static int BackgroundIndex(Rgba32 background, GifFramePalette palette)
    {
        if (background.A == 0)
        {
            return Math.Max(palette.TransparentIndex, 0);
        }

        for (int i = 0; i < palette.Colors.Length; i++)
        {
            if (i != palette.TransparentIndex && palette.Colors[i] == background with { A = 255 })
            {
                return i;
            }
        }

        return 0;
    }

    private static void WriteMetadata(Stream stream, Image image, GifEncoderOptions options)
    {
        if (Icc(image, options) is { } icc)
        {
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
            stream.Write(GifConstants.IccIdentifier);
            for (int pos = 0; pos < icc.Length; pos += 255)
            {
                int size = Math.Min(255, icc.Length - pos);
                stream.WriteByte((byte)size);
                stream.Write(icc, pos, size);
            }

            stream.WriteByte(0);
        }

        if (Xmp(image, options) is { } xmp)
        {
            // The packet is stored raw; the "magic trailer" (0x01, 0xFF, 0xFE, …, 0x00) lets sub-block readers skip it.
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.ApplicationLabel, 11]);
            stream.Write(GifConstants.XmpIdentifier);
            stream.Write(xmp);
            stream.WriteByte(1);
            for (int value = 255; value >= 0; value--)
            {
                stream.WriteByte((byte)value);
            }

            stream.WriteByte(0);
        }
    }

    private static byte[]? Icc(Image image, GifEncoderOptions options) =>
        (options.Metadata & MetadataKinds.Icc) != 0 && image.Metadata.IccProfile is { Length: > 0 } icc ? icc : null;

    private static byte[]? Xmp(Image image, GifEncoderOptions options) =>
        (options.Metadata & MetadataKinds.Xmp) != 0 && image.Metadata.Xmp is { Length: > 0 } xmp ? xmp : null;

    private static bool WriteFrame(Stream stream, PendingFrame frame, GifPaletteBuilder palettes, int canvasWidth, bool animated, GifEncoderOptions options)
    {
        GifFramePalette? palette = frame.Palette
            ?? palettes.BuildLocal(frame.Rendered, frame.Before, canvasWidth, (frame.X0, frame.Y0, frame.X1, frame.Y1), options.OptimizeFrames);
        if (palette is null)
        {
            return false;
        }

        int transparent = palette.TransparentIndex;
        int delay = animated ? (int)Math.Clamp(Math.Round(frame.DurationMs / 10, MidpointRounding.AwayFromZero), 0, ushort.MaxValue) : 0;
        if (animated || transparent >= 0 || frame.Disposal != GifDisposal.None)
        {
            int packed = ((int)frame.Disposal << 2) | (transparent >= 0 ? 1 : 0);
            stream.Write([GifConstants.ExtensionIntroducer, GifConstants.GraphicControlLabel, 4, (byte)packed,
                (byte)delay, (byte)(delay >> 8), (byte)Math.Max(transparent, 0), 0]);
        }

        int width = frame.X1 - frame.X0;
        int height = frame.Y1 - frame.Y0;
        int bits = TableBits(palette.Colors.Length);
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = GifConstants.ImageSeparator;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..], (ushort)frame.X0);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..], (ushort)frame.Y0);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)height);
        // The first frame's palette is the global color table in the header.
        bool local = !palette.IsGlobal && !frame.IsFirst;
        descriptor[9] = (byte)((local ? 0x80 | (bits - 1) : 0) | (options.Interlace ? 0x40 : 0));
        stream.Write(descriptor);
        if (local)
        {
            WriteColorTable(stream, palette.Colors, bits);
        }

        // Unchanged pixels become transparent, which keeps runs long for LZW.
        var indices = new byte[width * height];
        Rgba32[] before = frame.Before;
        Rgba32[] rendered = frame.Rendered;
        bool skipUnchanged = transparent >= 0 && options.OptimizeFrames;
        for (int y = 0; y < height; y++)
        {
            int outRow = (options.Interlace ? InterlacedRowAt(height, y) : y) * width;
            int canvasRow = ((frame.Y0 + y) * canvasWidth) + frame.X0;
            for (int x = 0; x < width; x++)
            {
                int p = canvasRow + x;
                indices[outRow + x] = skipUnchanged && rendered[p] == before[p] ? (byte)transparent : palette.Indices[p];
            }
        }

        GifLzwEncoder.Encode(indices, Math.Max(2, bits), stream);
        return true;
    }

    /// <summary>Number of bits for a color table holding <paramref name="count"/> entries (at least 2 entries).</summary>
    private static int TableBits(int count)
    {
        int bits = 1;
        while ((1 << bits) < count)
        {
            bits++;
        }

        return bits;
    }

    private static void WriteColorTable(Stream stream, Rgba32[] colors, int bits)
    {
        var table = new byte[3 << bits];
        for (int i = 0; i < colors.Length; i++)
        {
            table[3 * i] = colors[i].R;
            table[(3 * i) + 1] = colors[i].G;
            table[(3 * i) + 2] = colors[i].B;
        }

        stream.Write(table);
    }

    /// <summary>The position in the output of source row <paramref name="y"/> when rows are stored in four interlace passes.</summary>
    private static int InterlacedRowAt(int height, int y)
    {
        ReadOnlySpan<int> start = [0, 4, 2, 1];
        ReadOnlySpan<int> step = [8, 8, 4, 2];
        int before = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            if (y >= start[pass] && (y - start[pass]) % step[pass] == 0)
            {
                return before + ((y - start[pass]) / step[pass]);
            }

            before += height > start[pass] ? ((height - start[pass] - 1) / step[pass]) + 1 : 0;
        }

        return y;
    }
}
