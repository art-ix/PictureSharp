using System.Buffers;

namespace PictureSharp.Formats.Gif;

/// <summary>
/// Decoder for GIF87a/GIF89a files, including animations. Frames are composited onto full canvases with the
/// semantics of browsers and libwebp's gif2webp: the canvas starts transparent, transparent palette entries leave
/// the canvas untouched, "restore to background" clears to transparent and "restore to previous" restores the
/// canvas as it was before the frame was drawn.
/// </summary>
internal sealed class GifDecoder : IImageDecoder
{
    public static GifDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Gif;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        GifFile gif = GifFile.Parse(data.Span, options, readMetadata: false);
        return new ImageInfo
        {
            Format = ImageFormat.Gif,
            Width = gif.Width,
            Height = gif.Height,
            HasAlpha = gif.HasTransparency,
            FrameCount = gif.Frames.Count,
            IsAnimated = gif.Frames.Count > 1,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> span = data.Span;
        GifFile gif = GifFile.Parse(span, options, readMetadata: !options.IgnoreMetadata);
        int width = gif.Width;
        int height = gif.Height;
        int frameCount = options.FirstFrameOnly ? 1 : gif.Frames.Count;
        options.ValidateFrames(ImageFormat.Gif, width, height, frameCount);

        long canvasPixels = (long)width * height;
        var canvas = new Rgba32[canvasPixels];
        Rgba32[]? beforeFrame = null;
        var frames = new List<ImageFrame>(frameCount);
        for (int index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GifFrameInfo frame = gif.Frames[index];
            if (frame.Disposal == GifDisposal.Previous)
            {
                beforeFrame ??= new Rgba32[canvas.Length];
                canvas.AsSpan().CopyTo(beforeFrame);
            }

            DrawFrame(span, gif, frame, canvas, cancellationToken);
            // The last frame takes the canvas itself; its disposal no longer matters.
            bool last = index == frameCount - 1;
            frames.Add(new ImageFrame(width, height, last ? canvas : (Rgba32[])canvas.Clone())
            {
                Duration = TimeSpan.FromMilliseconds(frame.DelayCentiseconds * 10),
            });
            if (last)
            {
                break;
            }

            // port: examples/gifdec.c GIFDisposeFrame
            if (frame.Disposal != GifDisposal.None && Clip(frame, width, height) is var (x0, y0, x1, y1) && x1 > x0 && y1 > y0)
            {
                for (int y = y0; y < y1; y++)
                {
                    Span<Rgba32> row = canvas.AsSpan((y * width) + x0, x1 - x0);
                    if (frame.Disposal == GifDisposal.Background)
                    {
                        row.Clear();
                    }
                    else
                    {
                        beforeFrame.AsSpan((y * width) + x0, x1 - x0).CopyTo(row);
                    }
                }
            }
        }

        var image = new Image(frames)
        {
            SourceFormat = ImageFormat.Gif,
            HasAlpha = gif.HasTransparency,
            LoopCount = frames.Count > 1 ? LoopCount(gif.Repetitions) : 0,
            AnimationBackgroundColor = BackgroundColor(gif),
        };

        if (!options.IgnoreMetadata)
        {
            image.Metadata.IccProfile = gif.Icc;
            image.Metadata.Xmp = gif.Xmp;
        }

        return image;
    }

    /// <summary>Maps the NETSCAPE2.0 repeat count to the number of plays (0 = forever), like gif2webp.</summary>
    // port: examples/gif2webp.c main (loop count adaptation)
    internal static int LoopCount(int? repetitions) => repetitions switch
    {
        null => 1,
        0 => 0,
        >= 65535 => 65535,
        int n => n + 1,
    };

    // port: examples/gifdec.c GIFGetBackgroundColor
    private static Rgba32 BackgroundColor(GifFile gif)
    {
        int transparent = gif.Frames[0].TransparentIndex;
        if (transparent >= 0 && gif.BackgroundIndex == transparent)
        {
            return default;
        }

        Rgba32[]? table = gif.GlobalColorTable;
        return table is not null && gif.BackgroundIndex < table.Length ? table[gif.BackgroundIndex] : new Rgba32(255, 255, 255);
    }

    private static (int X0, int Y0, int X1, int Y1) Clip(GifFrameInfo frame, int width, int height) =>
        (Math.Min(frame.X, width), Math.Min(frame.Y, height), Math.Min(frame.X + frame.Width, width), Math.Min(frame.Y + frame.Height, height));

    /// <summary>Decodes one frame and draws its non-transparent pixels onto the canvas (clipped to the canvas).</summary>
    private static void DrawFrame(ReadOnlySpan<byte> data, GifFile gif, GifFrameInfo frame, Rgba32[] canvas, CancellationToken cancellationToken)
    {
        int frameWidth = frame.Width;
        int frameHeight = frame.Height;
        int pixelCount = frameWidth * frameHeight;
        if (pixelCount == 0)
        {
            return;
        }

        byte[] indices = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            int produced = GifLzwDecoder.Decode(data, frame.DataOffset, frame.MinCodeSize, indices.AsSpan(0, pixelCount), cancellationToken);
            Span<Rgba32> colors = stackalloc Rgba32[256];
            BuildPalette(frame.LocalColorTable ?? gif.GlobalColorTable, colors);

            int width = gif.Width;
            (int x0, _, int x1, int y1) = Clip(frame, width, gif.Height);
            int transparent = frame.TransparentIndex;
            int rowsProduced = (produced + frameWidth - 1) / frameWidth;
            for (int row = 0; row < frameHeight && row < rowsProduced; row++)
            {
                // Interlaced images store rows 0, 8, 16, …, then 4, 12, …, then 2, 6, …, then 1, 3, ….
                int y = frame.Interlaced ? InterlacedRow(frameHeight, row) : row;
                int canvasY = frame.Y + y;
                if (canvasY >= y1 || x1 <= x0)
                {
                    continue;
                }

                int start = row * frameWidth;
                int count = Math.Min(x1 - x0, produced - start);
                ReadOnlySpan<byte> src = indices.AsSpan(start, Math.Max(count, 0));
                Span<Rgba32> dst = canvas.AsSpan((canvasY * width) + x0);
                for (int x = 0; x < src.Length; x++)
                {
                    int index = src[x];
                    if (index != transparent)
                    {
                        dst[x] = colors[index];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(indices);
        }
    }

    /// <summary>
    /// Palette lookup for all 256 indices: entries past the table are opaque black and a missing table is read as
    /// grayscale, matching Pillow.
    /// </summary>
    private static void BuildPalette(Rgba32[]? table, Span<Rgba32> colors)
    {
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = table is null ? new Rgba32((byte)i, (byte)i, (byte)i)
                : i < table.Length ? table[i] : new Rgba32(0, 0, 0);
        }
    }

    /// <summary>Maps the <paramref name="row"/>-th stored row of an interlaced image to its position.</summary>
    private static int InterlacedRow(int height, int row)
    {
        ReadOnlySpan<int> start = [0, 4, 2, 1];
        ReadOnlySpan<int> step = [8, 8, 4, 2];
        int before = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            int rows = height > start[pass] ? ((height - start[pass] - 1) / step[pass]) + 1 : 0;
            if (row < before + rows)
            {
                return start[pass] + ((row - before) * step[pass]);
            }

            before += rows;
        }

        return height;
    }
}
