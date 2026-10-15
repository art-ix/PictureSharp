using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using PictureSharp.Compression;
using PictureSharp.Formats.Metadata;
using PictureSharp.Internal;

namespace PictureSharp.Formats.Png;

/// <summary>Decoder for PNG and APNG files.</summary>
internal sealed partial class PngDecoder : IImageDecoder
{
    public static PngDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Png;

    private static ImageFormatException Error(string message, Exception? inner = null) => new(ImageFormat.Png, message, inner);

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        PngFile png = PngFile.Parse(data.Span, options, metadataOnly: true);
        return new ImageInfo
        {
            Format = ImageFormat.Png,
            Width = png.Width,
            Height = png.Height,
            HasAlpha = png.HasAlpha,
            FrameCount = png.Animation is { } a ? a.Frames.Count : 1,
            IsAnimated = png.Animation is { Frames.Count: > 1 },
            BitsPerChannel = png.BitDepth,
            Orientation = png.Exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(png.Exif),
            HorizontalResolution = png.DpiX ?? 96f,
            VerticalResolution = png.DpiY ?? 96f,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> span = data.Span;
        PngFile png = PngFile.Parse(span, options, metadataOnly: false);
        bool animated = png.Animation is { Frames.Count: > 0 } && !options.FirstFrameOnly;
        options.ValidateFrames(ImageFormat.Png, png.Width, png.Height, animated ? png.Animation!.Frames.Count : 1);

        List<ImageFrame> frames;
        int loopCount = 0;
        if (animated && png.Animation is { } anim)
        {
            frames = DecodeAnimation(span, png, anim, options.MaxDegreeOfParallelism, cancellationToken);
            loopCount = anim.NumPlays;
        }
        else
        {
            if (png.Idat.Count == 0)
            {
                throw Error("Missing IDAT chunk.");
            }

            var frame = new ImageFrame(png.Width, png.Height, DecodeImage(span, png, png.Idat, 0, png.Width, png.Height, options.MaxDegreeOfParallelism, cancellationToken));
            frames = [frame];
        }

        var image = new Image(frames)
        {
            SourceFormat = ImageFormat.Png,
            HasAlpha = png.HasAlpha,
            LoopCount = loopCount,
        };

        if (png.DpiX is float dx && png.DpiY is float dy && dx > 0 && dy > 0)
        {
            (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution) = (dx, dy);
        }

        if (!options.IgnoreMetadata)
        {
            image.Metadata.IccProfile = png.Icc;
            image.Metadata.Exif = png.Exif;
            image.Metadata.Xmp = png.Xmp;
        }

        return image;
    }

    // ------------------------------------------------------------------ pixel data

    /// <summary>Inflates, unfilters and converts one image (or APNG frame) to RGBA.</summary>
    private static Rgba32[] DecodeImage(ReadOnlySpan<byte> file, PngFile png, List<(int Offset, int Length)> parts, int skip, int width, int height,
        int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        // Concatenate the compressed data (fdAT payloads start with a 4-byte sequence number).
        int total = 0;
        foreach ((_, int length) in parts)
        {
            total += length - skip;
        }

        int bpp = PngConstants.Channels(png.Color) * png.BitDepth;
        long expected = 0;
        if (png.Interlaced)
        {
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = PngConstants.PassWidth(width, pass);
                int ph = PngConstants.PassHeight(height, pass);
                if (pw > 0 && ph > 0)
                {
                    expected += ph * (1 + PngConstants.RowBytes(pw, bpp));
                }
            }
        }
        else
        {
            expected = height * (1 + PngConstants.RowBytes(width, bpp));
        }

        if (expected > Array.MaxLength)
        {
            throw Error("Image is too large.");
        }

        // The compressed bytes live in a pooled buffer until the pixels are converted.
        byte[] compressed = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            int pos = 0;
            foreach ((int offset, int length) in parts)
            {
                file.Slice(offset + skip, length - skip).CopyTo(compressed.AsSpan(pos));
                pos += length - skip;
            }

            var rowConverter = new RowConverter(png);
            return !png.Interlaced && Parallelism.IsWorthwhile(maxDegreeOfParallelism, expected, PipelineMinimumBytes)
                ? DecodePipelined(compressed, total, png, width, height, bpp, (int)expected, rowConverter, cancellationToken)
                : DecodeSequential(compressed, total, png, width, height, bpp, (int)expected, rowConverter, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(compressed);
        }
    }

    private static Rgba32[] DecodeSequential(byte[] compressed, int compressedLength, PngFile png, int width, int height, int bpp, int expected,
        RowConverter rowConverter, CancellationToken cancellationToken)
    {
        Inflater.PooledOutput inflated;
        try
        {
            inflated = Inflater.InflateZlibPooled(compressed, 0, compressedLength, expected, expected + (1L << 20),
                verifyChecksum: png.StrictChecksums, stopAtExpectedSize: true, cancellationToken: cancellationToken);
        }
        catch (InvalidDataStreamException ex)
        {
            throw Error("Corrupt image data: " + ex.Message, ex);
        }

        try
        {
            if (inflated.Length < expected)
            {
                throw Error("Not enough image data.");
            }

            byte[] raw = inflated.Data;
            var pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
            if (!png.Interlaced)
            {
                Unfilter(raw, 0, width, height, bpp);
                int rowBytes = (int)PngConstants.RowBytes(width, bpp);
                for (int y = 0; y < height; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rowConverter.Convert(raw.AsSpan((y * (rowBytes + 1)) + 1, rowBytes), pixels.AsSpan(y * width, width));
                }

                return pixels;
            }

            int offset = 0;
            var tmp = new Rgba32[width];
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = PngConstants.PassWidth(width, pass);
                int ph = PngConstants.PassHeight(height, pass);
                if (pw <= 0 || ph <= 0)
                {
                    continue;
                }

                Unfilter(raw, offset, pw, ph, bpp);
                int rowBytes = (int)PngConstants.RowBytes(pw, bpp);
                int x0 = PngConstants.Adam7XStart[pass], dx = PngConstants.Adam7XStep[pass];
                int y0 = PngConstants.Adam7YStart[pass], dy = PngConstants.Adam7YStep[pass];
                for (int j = 0; j < ph; j++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rowConverter.Convert(raw.AsSpan(offset + (j * (rowBytes + 1)) + 1, rowBytes), tmp.AsSpan(0, pw));
                    int rowStart = ((y0 + (j * dy)) * width) + x0;
                    for (int i = 0; i < pw; i++)
                    {
                        pixels[rowStart + (i * dx)] = tmp[i];
                    }
                }

                offset += ph * (rowBytes + 1);
            }

            return pixels;
        }
        finally
        {
            inflated.Return();
        }
    }

    /// <summary>Reverses the scanline filters in place. Each row is preceded by its filter type byte.</summary>
    internal static void Unfilter(byte[] data, int offset, int width, int height, int bitsPerPixel)
    {
        WalkRows(data, offset, width, height, bitsPerPixel, UnfilterRow);
    }

    /// <summary>Scalar scanline reconstruction. The vector path is in <c>PngDecoder.Simd.cs</c>.</summary>
    internal static void UnfilterScalar(byte[] data, int offset, int width, int height, int bitsPerPixel) =>
        WalkRows(data, offset, width, height, bitsPerPixel, UnfilterRowScalar);

    /// <summary>Reverses the filter of one row; <paramref name="previous"/> is empty for the first row.</summary>
    private static void UnfilterRow(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter)
    {
        if (Vector128.IsHardwareAccelerated)
        {
            UnfilterRowSimd(row, previous, bpp, filter);
        }
        else
        {
            UnfilterRowScalar(row, previous, bpp, filter);
        }
    }

    private static void UnfilterRowScalar(Span<byte> row, ReadOnlySpan<byte> prev, int bpp, int filter)
    {
        switch (filter)
        {
            case 1:
                UnfilterSubScalar(row, bpp);
                break;
            case 2:
                if (!prev.IsEmpty)
                {
                    UnfilterUpScalar(row, prev);
                }

                break;
            case 3:
                UnfilterAverageScalar(row, prev, bpp);
                break;
            case 4:
                UnfilterPaethScalar(row, prev, bpp);
                break;
            case not 0:
                throw Error($"Invalid scanline filter type {filter}.");
        }
    }

    private static void UnfilterSubScalar(Span<byte> row, int bpp)
    {
        if (bpp is 3 or 4)
        {
            UnfilterSubPixels(row, bpp);
            return;
        }

        for (int i = bpp; i < row.Length; i++)
        {
            row[i] += row[i - bpp];
        }
    }

    private static void UnfilterUpScalar(Span<byte> row, ReadOnlySpan<byte> previous)
    {
        for (int i = 0; i < row.Length; i++)
        {
            row[i] += previous[i];
        }
    }

    private static void UnfilterAverageScalar(Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        if (bpp is 3 or 4)
        {
            UnfilterAveragePixels(row, previous, bpp);
            return;
        }

        if (previous.IsEmpty)
        {
            for (int i = bpp; i < row.Length; i++)
            {
                row[i] += (byte)(row[i - bpp] >> 1);
            }

            return;
        }

        int head = Math.Min(bpp, row.Length);
        for (int i = 0; i < head; i++)
        {
            row[i] += (byte)(previous[i] >> 1);
        }

        for (int i = bpp; i < row.Length; i++)
        {
            row[i] += (byte)((row[i - bpp] + previous[i]) >> 1);
        }
    }

    private static void UnfilterPaethScalar(Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        // Without a previous row Paeth predicts the left byte; in the first pixel it predicts the byte above.
        if (previous.IsEmpty)
        {
            UnfilterSubScalar(row, bpp);
            return;
        }

        int head = Math.Min(bpp, row.Length);
        for (int i = 0; i < head; i++)
        {
            row[i] += previous[i];
        }

        for (int i = bpp; i < row.Length; i++)
        {
            row[i] += PngConstants.Paeth(row[i - bpp], previous[i], previous[i - bpp]);
        }
    }

    // Sub for 3 and 4 bytes per pixel, a pixel at a time (per-byte addition in a uint).
    private static void UnfilterSubPixels(Span<byte> row, int bpp)
    {
        uint left = 0;
        int last = row.Length - bpp;
        for (int i = 0; i <= last; i += bpp)
        {
            left = AddBytes(LoadPixel(row, i, bpp), left);
            StorePixel(row, i, bpp, left);
        }
    }

    private static void UnfilterAveragePixels(Span<byte> row, ReadOnlySpan<byte> previous, int bpp)
    {
        uint left = 0;
        int last = row.Length - bpp;
        for (int i = 0; i <= last; i += bpp)
        {
            uint up = previous.IsEmpty ? 0 : LoadPixel(previous, i, bpp);
            left = AddBytes(LoadPixel(row, i, bpp), AverageBytes(left, up));
            StorePixel(row, i, bpp, left);
        }
    }

    // Per-byte (x + y) mod 256 of four packed bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AddBytes(uint x, uint y) => ((x & 0x7f7f7f7fu) + (y & 0x7f7f7f7fu)) ^ ((x ^ y) & 0x80808080u);

    // Per-byte (a + b) >> 1 of four packed bytes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint AverageBytes(uint a, uint b) => (a & b) + (((a ^ b) >> 1) & 0x7f7f7f7fu);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint LoadPixel(ReadOnlySpan<byte> data, int i, int bpp) =>
        bpp == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) : (uint)(BinaryPrimitives.ReadUInt16LittleEndian(data[i..]) | (data[i + 2] << 16));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StorePixel(Span<byte> data, int i, int bpp, uint value)
    {
        if (bpp == 4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data[i..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data[i..], (ushort)value);
            data[i + 2] = (byte)(value >> 16);
        }
    }

    private static void WalkRows(byte[] data, int offset, int width, int height, int bitsPerPixel, RowFilter filter)
    {
        int rowBytes = (int)PngConstants.RowBytes(width, bitsPerPixel);
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int stride = rowBytes + 1;
        for (int y = 0; y < height; y++)
        {
            int rowStart = offset + (y * stride);
            Span<byte> row = data.AsSpan(rowStart + 1, rowBytes);
            ReadOnlySpan<byte> prev = y == 0 ? default : data.AsSpan(rowStart - stride + 1, rowBytes);
            filter(row, prev, bpp, data[rowStart]);
        }
    }

    private delegate void RowFilter(Span<byte> row, ReadOnlySpan<byte> previous, int bpp, int filter);
}
