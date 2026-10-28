using PictureSharp.Application.Decoding;
using System.Buffers;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Sequences;

namespace PictureSharp.Infrastructure.Codecs.Gif;

/// <summary>
/// Decoder for GIF87a/GIF89a files, including animations. Frames are composited onto full canvases with the
/// semantics of browsers and libwebp's gif2webp: the canvas starts transparent, transparent palette entries leave
/// the canvas untouched, "restore to background" clears to transparent and "restore to previous" restores the
/// canvas as it was before the frame was drawn.
/// </summary>
internal sealed class GifDecoder : IImageDecoder, ISequentialImageDecoder
{
    public static GifDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Gif;

    public bool SupportsNonSeekableInput => true;

    public ValueTask<FrameReadSession> OpenAsync(Stream input, DecodeContext context, bool leaveOpen, CancellationToken cancellationToken) =>
        GifFrameReader.OpenAsync(input, context, leaveOpen, cancellationToken);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        GifFile gif = GifFile.Parse(data.Span, options, readMetadata: false);
        return new ImageInfo
        {
            Format = ImageFormat.Gif,
            Width = gif.Width,
            Height = gif.Height,
            Color = new ImageColorInfo
            {
                Model = ImageColorModel.Palette,
                ChannelCount = 1,
                BitsPerSample = gif.IndexBits,
                SampleFormat = ImageSampleFormat.UnsignedInteger,
                AlphaMode = gif.HasTransparency ? ImageAlphaMode.Straight : ImageAlphaMode.None,
                HasIccProfile = gif.HasIccExtension,
            },
            Compression = ImageCompression.Lossless,
            FrameCount = gif.Frames.Count,
            IsAnimated = gif.Frames.Count > 1,
        };
    }

    public DecodedImage Decode(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        context.CheckInput(data.Length);
        Image image = DecodeCore(data, context.Options, context.CancellationToken, context);
        return new(image, image.Size);
    }

    private Image DecodeCore(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken, DecodeContext context)
    {
        ReadOnlySpan<byte> span = data.Span;
        GifFile gif = GifFile.Parse(span, options, readMetadata: !options.IgnoreMetadata);
        context.Budget.ReserveMetadata(ImageFormat.Gif, (long)(gif.Icc?.Length ?? 0) + (gif.Xmp?.Length ?? 0));
        int width = gif.Width;
        int height = gif.Height;
        int frameCount = options.FirstFrameOnly ? 1 : gif.Frames.Count;
        context.Budget.ReserveImage(ImageFormat.Gif, width, height, frameCount);
        // A frame may overhang a small canvas; its index buffer still needs the declared full frame size.
        for (int i = 0; i < frameCount; i++)
        {
            GifFrameInfo frame = gif.Frames[i];
            if (frame.Width > 0 && frame.Height > 0)
                options.ValidateDimensions(ImageFormat.Gif, frame.Width, frame.Height);
        }

        var canvas = new GifCanvas(width, height);
        var frames = new List<ImageFrame>(frameCount);
        for (int index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GifFrameInfo frame = gif.Frames[index];
            canvas.Draw(span, gif.GlobalColorTable, frame, cancellationToken);
            // The last frame takes the canvas itself; its disposal no longer matters.
            bool last = index == frameCount - 1;
            frames.Add(new ImageFrame(width, height, last ? canvas.Pixels : (Rgba32[])canvas.Pixels.Clone())
            {
                Duration = Duration(frame),
            });
            if (last)
            {
                break;
            }

            canvas.ApplyDisposal(frame);
        }

        var image = new Image(frames)
        {
            SourceFormat = ImageFormat.Gif,
            HasAlpha = gif.HasTransparency,
            LoopCount = frames.Count > 1 ? LoopCount(gif.Repetitions) : 0,
            AnimationBackgroundColor = BackgroundColor(gif.Frames[0].TransparentIndex, gif.BackgroundIndex, gif.GlobalColorTable),
        };

        if (!options.IgnoreMetadata)
        {
            image.Metadata.IccData = gif.Icc;
            image.Metadata.XmpData = gif.Xmp;
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

    internal static TimeSpan Duration(GifFrameInfo frame) => TimeSpan.FromMilliseconds(frame.DelayCentiseconds * 10);

    // port: examples/gifdec.c GIFGetBackgroundColor
    internal static Rgba32 BackgroundColor(int firstTransparentIndex, int backgroundIndex, Rgba32[]? globalTable)
    {
        if (firstTransparentIndex >= 0 && backgroundIndex == firstTransparentIndex)
        {
            return default;
        }

        return globalTable is not null && backgroundIndex < globalTable.Length ? globalTable[backgroundIndex] : new Rgba32(255, 255, 255);
    }

    internal static (int X0, int Y0, int X1, int Y1) Clip(GifFrameInfo frame, int width, int height) =>
        (Math.Min(frame.X, width), Math.Min(frame.Y, height), Math.Min(frame.X + frame.Width, width), Math.Min(frame.Y + frame.Height, height));

    /// <summary>Decodes one frame and draws its non-transparent pixels onto the canvas (clipped to the canvas).</summary>
    internal static void DrawFrame(ReadOnlySpan<byte> data, Rgba32[]? globalTable, int width, int height, GifFrameInfo frame, Rgba32[] canvas,
        CancellationToken cancellationToken)
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
            BuildPalette(frame.LocalColorTable ?? globalTable, colors);

            (int x0, _, int x1, int y1) = Clip(frame, width, height);
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
