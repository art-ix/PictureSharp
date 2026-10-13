using RasterSharp.Compression;
using RasterSharp.Formats.Metadata;

namespace RasterSharp.Formats.Png;

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

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> span = data.Span;
        PngFile png = PngFile.Parse(span, options, metadataOnly: false);
        options.ValidateDimensions(ImageFormat.Png, png.Width, png.Height);

        List<ImageFrame> frames;
        int loopCount = 0;
        if (png.Animation is { } anim && anim.Frames.Count > 0 && !options.FirstFrameOnly)
        {
            frames = DecodeAnimation(span, png, anim);
            loopCount = anim.NumPlays;
        }
        else
        {
            if (png.Idat.Count == 0)
            {
                throw Error("Missing IDAT chunk.");
            }

            var frame = new ImageFrame(png.Width, png.Height, DecodeImage(span, png, png.Idat, 0, png.Width, png.Height));
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
    private static Rgba32[] DecodeImage(ReadOnlySpan<byte> file, PngFile png, List<(int Offset, int Length)> parts, int skip, int width, int height)
    {
        // Concatenate the compressed data (fdAT payloads start with a 4-byte sequence number).
        int total = 0;
        foreach ((_, int length) in parts)
        {
            total += length - skip;
        }

        var compressed = new byte[total];
        int pos = 0;
        foreach ((int offset, int length) in parts)
        {
            file.Slice(offset + skip, length - skip).CopyTo(compressed.AsSpan(pos));
            pos += length - skip;
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

        byte[] raw;
        try
        {
            raw = Inflater.InflateZlib(compressed, 0, compressed.Length, (int)expected, expected + (1 << 20),
                verifyChecksum: png.StrictChecksums, stopAtExpectedSize: true);
        }
        catch (InvalidDataStreamException ex)
        {
            throw Error("Corrupt image data: " + ex.Message, ex);
        }

        if (raw.Length < expected)
        {
            throw Error("Not enough image data.");
        }

        var pixels = GC.AllocateUninitializedArray<Rgba32>(width * height);
        var rowConverter = new RowConverter(png);
        if (!png.Interlaced)
        {
            Unfilter(raw, 0, width, height, bpp);
            int rowBytes = (int)PngConstants.RowBytes(width, bpp);
            for (int y = 0; y < height; y++)
            {
                rowConverter.Convert(raw.AsSpan((y * (rowBytes + 1)) + 1, rowBytes), pixels.AsSpan(y * width, width));
            }
        }
        else
        {
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
                    rowConverter.Convert(raw.AsSpan(offset + (j * (rowBytes + 1)) + 1, rowBytes), tmp.AsSpan(0, pw));
                    int rowStart = ((y0 + (j * dy)) * width) + x0;
                    for (int i = 0; i < pw; i++)
                    {
                        pixels[rowStart + (i * dx)] = tmp[i];
                    }
                }

                offset += ph * (rowBytes + 1);
            }
        }

        return pixels;
    }

    /// <summary>Reverses the scanline filters in place. Each row is preceded by its filter type byte.</summary>
    private static void Unfilter(byte[] data, int offset, int width, int height, int bitsPerPixel)
    {
        int rowBytes = (int)PngConstants.RowBytes(width, bitsPerPixel);
        int bpp = Math.Max(1, bitsPerPixel / 8);
        int stride = rowBytes + 1;
        for (int y = 0; y < height; y++)
        {
            int rowStart = offset + (y * stride);
            int filter = data[rowStart];
            Span<byte> row = data.AsSpan(rowStart + 1, rowBytes);
            ReadOnlySpan<byte> prev = y == 0 ? default : data.AsSpan(rowStart - stride + 1, rowBytes);
            switch (filter)
            {
                case 0:
                    break;
                case 1: // Sub
                    for (int i = bpp; i < row.Length; i++)
                    {
                        row[i] += row[i - bpp];
                    }

                    break;
                case 2: // Up
                    if (!prev.IsEmpty)
                    {
                        for (int i = 0; i < row.Length; i++)
                        {
                            row[i] += prev[i];
                        }
                    }

                    break;
                case 3: // Average
                    for (int i = 0; i < row.Length; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        int up = prev.IsEmpty ? 0 : prev[i];
                        row[i] += (byte)((left + up) >> 1);
                    }

                    break;
                case 4: // Paeth
                    for (int i = 0; i < row.Length; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0;
                        int up = prev.IsEmpty ? 0 : prev[i];
                        int upLeft = i >= bpp && !prev.IsEmpty ? prev[i - bpp] : 0;
                        row[i] += PngConstants.Paeth(left, up, upLeft);
                    }

                    break;
                default:
                    throw Error($"Invalid scanline filter type {filter}.");
            }
        }
    }
}
