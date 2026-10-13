using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using RasterSharp.Compression;
using RasterSharp.Formats.Metadata;

namespace RasterSharp.Formats.Png;

/// <summary>PNG color types (IHDR).</summary>
internal enum PngColor : byte
{
    Gray = 0,
    Rgb = 2,
    Palette = 3,
    GrayAlpha = 4,
    Rgba = 6,
}

/// <summary>Shared PNG constants and helpers.</summary>
internal static class PngConstants
{
    public static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Adam7 pass geometry: x start, y start, x step, y step.
    public static ReadOnlySpan<byte> Adam7XStart => [0, 4, 0, 2, 0, 1, 0];
    public static ReadOnlySpan<byte> Adam7YStart => [0, 0, 4, 0, 2, 0, 1];
    public static ReadOnlySpan<byte> Adam7XStep => [8, 8, 4, 4, 2, 2, 1];
    public static ReadOnlySpan<byte> Adam7YStep => [8, 8, 8, 4, 4, 2, 2];

    public const string XmpKeyword = "XML:com.adobe.xmp";

    public static int Channels(PngColor color) => color switch
    {
        PngColor.Gray => 1,
        PngColor.Rgb => 3,
        PngColor.Palette => 1,
        PngColor.GrayAlpha => 2,
        PngColor.Rgba => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    public static int PassWidth(int width, int pass) =>
        (width - Adam7XStart[pass] + Adam7XStep[pass] - 1) / Adam7XStep[pass];

    public static int PassHeight(int height, int pass) =>
        (height - Adam7YStart[pass] + Adam7YStep[pass] - 1) / Adam7YStep[pass];

    public static long RowBytes(int width, int bitsPerPixel) => (((long)width * bitsPerPixel) + 7) / 8;

    public static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }
}

/// <summary>Decoder for PNG and APNG files.</summary>
internal sealed class PngDecoder : IImageDecoder
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

    // ------------------------------------------------------------------ APNG

    private static List<ImageFrame> DecodeAnimation(ReadOnlySpan<byte> file, PngFile png, PngAnimation anim)
    {
        int width = png.Width;
        int height = png.Height;
        var canvas = new Rgba32[width * height];
        var frames = new List<ImageFrame>(anim.Frames.Count);

        for (int index = 0; index < anim.Frames.Count; index++)
        {
            ApngFrame f = anim.Frames[index];
            if (f.Width <= 0 || f.Height <= 0 || f.X < 0 || f.Y < 0 ||
                (long)f.X + f.Width > width || (long)f.Y + f.Height > height)
            {
                throw Error("APNG frame lies outside the image.");
            }

            if (f.Parts.Count == 0)
            {
                throw Error("APNG frame has no image data.");
            }

            Rgba32[] pixels = DecodeImage(file, png, f.Parts, f.IsDefaultImage ? 0 : 4, f.Width, f.Height);

            // The first frame's "previous" disposal is treated as "background".
            int dispose = index == 0 && f.DisposeOp == 2 ? 1 : f.DisposeOp;
            Rgba32[]? saved = null;
            if (dispose == 2)
            {
                saved = CopyRegion(canvas, width, f);
            }

            for (int y = 0; y < f.Height; y++)
            {
                Span<Rgba32> dst = canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width);
                ReadOnlySpan<Rgba32> src = pixels.AsSpan(y * f.Width, f.Width);
                if (f.BlendOp == 0)
                {
                    src.CopyTo(dst);
                }
                else
                {
                    for (int x = 0; x < f.Width; x++)
                    {
                        dst[x] = BlendOver(src[x], dst[x]);
                    }
                }
            }

            frames.Add(new ImageFrame(width, height, (Rgba32[])canvas.Clone())
            {
                Duration = TimeSpan.FromMilliseconds(f.DelayDen == 0 ? f.DelayNum * 10.0 : f.DelayNum * 1000.0 / f.DelayDen),
            });

            if (dispose == 1)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width).Clear();
                }
            }
            else if (dispose == 2)
            {
                for (int y = 0; y < f.Height; y++)
                {
                    saved.AsSpan(y * f.Width, f.Width).CopyTo(canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width));
                }
            }
        }

        return frames;
    }

    private static Rgba32[] CopyRegion(Rgba32[] canvas, int width, ApngFrame f)
    {
        var copy = new Rgba32[f.Width * f.Height];
        for (int y = 0; y < f.Height; y++)
        {
            canvas.AsSpan(((f.Y + y) * width) + f.X, f.Width).CopyTo(copy.AsSpan(y * f.Width));
        }

        return copy;
    }

    /// <summary>Porter–Duff "source over" for straight-alpha pixels.</summary>
    internal static Rgba32 BlendOver(Rgba32 src, Rgba32 dst)
    {
        if (src.A == 255 || dst.A == 0)
        {
            return src;
        }

        if (src.A == 0)
        {
            return dst;
        }

        int sa = src.A;
        int dstWeight = dst.A * (255 - sa);           // scaled by 255
        int outA255 = (sa * 255) + dstWeight;         // out alpha scaled by 255
        int outA = (outA255 + 127) / 255;
        return new Rgba32(
            (byte)(((src.R * sa * 255) + (dst.R * dstWeight) + (outA255 / 2)) / outA255),
            (byte)(((src.G * sa * 255) + (dst.G * dstWeight) + (outA255 / 2)) / outA255),
            (byte)(((src.B * sa * 255) + (dst.B * dstWeight) + (outA255 / 2)) / outA255),
            (byte)outA);
    }

    // ------------------------------------------------------------------ row conversion

    private sealed class RowConverter
    {
        private readonly PngColor _color;
        private readonly int _depth;
        private readonly Rgba32[] _palette = new Rgba32[256];
        private readonly int _trnsGray = -1;
        private readonly int _trnsR = -1, _trnsG = -1, _trnsB = -1;

        public RowConverter(PngFile png)
        {
            _color = png.Color;
            _depth = png.BitDepth;
            if (_color == PngColor.Palette)
            {
                // Out-of-range indices decode as opaque black.
                _palette.AsSpan().Fill(new Rgba32(0, 0, 0, 255));
                byte[] plte = png.Palette ?? throw Error("Missing PLTE chunk.");
                for (int i = 0; i < plte.Length / 3; i++)
                {
                    _palette[i] = new Rgba32(plte[3 * i], plte[(3 * i) + 1], plte[(3 * i) + 2], 255);
                }

                if (png.Transparency is { } trns)
                {
                    for (int i = 0; i < Math.Min(trns.Length, 256); i++)
                    {
                        _palette[i].A = trns[i];
                    }
                }
            }
            else if (png.Transparency is { } trns)
            {
                if (_color == PngColor.Gray && trns.Length >= 2)
                {
                    _trnsGray = (trns[0] << 8) | trns[1];
                }
                else if (_color == PngColor.Rgb && trns.Length >= 6)
                {
                    _trnsR = (trns[0] << 8) | trns[1];
                    _trnsG = (trns[2] << 8) | trns[3];
                    _trnsB = (trns[4] << 8) | trns[5];
                }
            }
        }

        private static byte Scale16(int v) => (byte)((v + 128) / 257);

        public void Convert(ReadOnlySpan<byte> row, Span<Rgba32> dst)
        {
            int n = dst.Length;
            switch (_color)
            {
                case PngColor.Gray when _depth < 8:
                {
                    int mask = (1 << _depth) - 1;
                    int scale = 255 / mask;
                    int perByte = 8 / _depth;
                    for (int x = 0; x < n; x++)
                    {
                        int shift = 8 - (_depth * ((x % perByte) + 1));
                        int v = (row[x / perByte] >> shift) & mask;
                        byte g = (byte)(v * scale);
                        dst[x] = new Rgba32(g, g, g, v == _trnsGray ? (byte)0 : (byte)255);
                    }

                    break;
                }

                case PngColor.Gray when _depth == 8:
                    for (int x = 0; x < n; x++)
                    {
                        byte g = row[x];
                        dst[x] = new Rgba32(g, g, g, g == _trnsGray ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Gray:
                    for (int x = 0; x < n; x++)
                    {
                        int v = (row[2 * x] << 8) | row[(2 * x) + 1];
                        byte g = Scale16(v);
                        dst[x] = new Rgba32(g, g, g, v == _trnsGray ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Rgb when _depth == 8:
                    for (int x = 0, i = 0; x < n; x++, i += 3)
                    {
                        byte r = row[i], g = row[i + 1], b = row[i + 2];
                        bool transparent = r == _trnsR && g == _trnsG && b == _trnsB;
                        dst[x] = new Rgba32(r, g, b, transparent ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Rgb:
                    for (int x = 0, i = 0; x < n; x++, i += 6)
                    {
                        int r = (row[i] << 8) | row[i + 1];
                        int g = (row[i + 2] << 8) | row[i + 3];
                        int b = (row[i + 4] << 8) | row[i + 5];
                        bool transparent = r == _trnsR && g == _trnsG && b == _trnsB;
                        dst[x] = new Rgba32(Scale16(r), Scale16(g), Scale16(b), transparent ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Palette:
                {
                    if (_depth == 8)
                    {
                        for (int x = 0; x < n; x++)
                        {
                            dst[x] = _palette[row[x]];
                        }
                    }
                    else
                    {
                        int mask = (1 << _depth) - 1;
                        int perByte = 8 / _depth;
                        for (int x = 0; x < n; x++)
                        {
                            int shift = 8 - (_depth * ((x % perByte) + 1));
                            dst[x] = _palette[(row[x / perByte] >> shift) & mask];
                        }
                    }

                    break;
                }

                case PngColor.GrayAlpha when _depth == 8:
                    for (int x = 0; x < n; x++)
                    {
                        byte g = row[2 * x];
                        dst[x] = new Rgba32(g, g, g, row[(2 * x) + 1]);
                    }

                    break;
                case PngColor.GrayAlpha:
                    for (int x = 0, i = 0; x < n; x++, i += 4)
                    {
                        byte g = Scale16((row[i] << 8) | row[i + 1]);
                        dst[x] = new Rgba32(g, g, g, Scale16((row[i + 2] << 8) | row[i + 3]));
                    }

                    break;
                case PngColor.Rgba when _depth == 8:
                    MemoryMarshal.Cast<byte, Rgba32>(row[..(n * 4)]).CopyTo(dst);
                    break;
                case PngColor.Rgba:
                    for (int x = 0, i = 0; x < n; x++, i += 8)
                    {
                        dst[x] = new Rgba32(
                            Scale16((row[i] << 8) | row[i + 1]),
                            Scale16((row[i + 2] << 8) | row[i + 3]),
                            Scale16((row[i + 4] << 8) | row[i + 5]),
                            Scale16((row[i + 6] << 8) | row[i + 7]));
                    }

                    break;
            }
        }
    }

    // ------------------------------------------------------------------ chunk parsing

    private sealed class ApngFrame
    {
        public int Sequence;
        public int Width;
        public int Height;
        public int X;
        public int Y;
        public int DelayNum;
        public int DelayDen;
        public int DisposeOp;
        public int BlendOp;
        public bool IsDefaultImage;
        public readonly List<(int Offset, int Length)> Parts = [];
    }

    private sealed class PngAnimation
    {
        public int NumFrames;
        public int NumPlays;
        public readonly List<ApngFrame> Frames = [];
    }

    private sealed class PngFile
    {
        public int Width;
        public int Height;
        public int BitDepth;
        public PngColor Color;
        public bool Interlaced;
        public byte[]? Palette;
        public byte[]? Transparency;
        public byte[]? Icc;
        public byte[]? Exif;
        public byte[]? Xmp;
        public float? DpiX;
        public float? DpiY;
        public bool StrictChecksums;
        public PngAnimation? Animation;
        public readonly List<(int Offset, int Length)> Idat = [];

        public bool HasAlpha => Color is PngColor.GrayAlpha or PngColor.Rgba || Transparency is not null;

        public static PngFile Parse(ReadOnlySpan<byte> data, DecoderOptions options, bool metadataOnly)
        {
            if (!data.StartsWith(PngConstants.Signature))
            {
                throw Error("Missing PNG signature.");
            }

            var png = new PngFile { StrictChecksums = options.StrictChecksums };
            int pos = 8;
            bool seenIhdr = false;
            bool seenIdat = false;
            bool seenEnd = false;
            ApngFrame? currentFrame = null;
            bool animationValid = true;

            while (pos + 12 <= data.Length)
            {
                uint length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
                if (length > int.MaxValue || length > (uint)(data.Length - pos - 12))
                {
                    if (seenIdat && !metadataOnly && png.Idat.Count > 0)
                    {
                        break;  // tolerate a truncated trailing chunk after the image data
                    }

                    throw Error("Invalid or truncated chunk.");
                }

                ReadOnlySpan<byte> type = data.Slice(pos + 4, 4);
                int payload = pos + 8;
                ReadOnlySpan<byte> body = data.Slice(payload, (int)length);
                if (options.StrictChecksums)
                {
                    uint crc = BinaryPrimitives.ReadUInt32BigEndian(data[(payload + (int)length)..]);
                    if (Crc32.Compute(data.Slice(pos + 4, (int)length + 4)) != crc)
                    {
                        throw Error($"CRC mismatch in chunk '{Encoding.ASCII.GetString(type)}'.");
                    }
                }

                pos = payload + (int)length + 4;

                if (!seenIhdr)
                {
                    if (!type.SequenceEqual("IHDR"u8))
                    {
                        throw Error("The first chunk must be IHDR.");
                    }

                    png.ReadHeader(body);
                    seenIhdr = true;
                    continue;
                }

                if (type.SequenceEqual("IDAT"u8))
                {
                    if (png.Color == PngColor.Palette && png.Palette is null)
                    {
                        throw Error("Missing PLTE chunk.");
                    }

                    seenIdat = true;
                    png.Idat.Add((payload, (int)length));
                    if (currentFrame is { IsDefaultImage: true })
                    {
                        currentFrame.Parts.Add((payload, (int)length));
                    }
                }
                else if (type.SequenceEqual("IEND"u8))
                {
                    seenEnd = true;
                    break;
                }
                else if (type.SequenceEqual("PLTE"u8))
                {
                    if (length % 3 != 0 || length == 0 || length > 768)
                    {
                        throw Error("Invalid PLTE chunk.");
                    }

                    png.Palette = body.ToArray();
                }
                else if (type.SequenceEqual("tRNS"u8))
                {
                    png.Transparency = body.ToArray();
                }
                else if (type.SequenceEqual("pHYs"u8) && length >= 9)
                {
                    if (body[8] == 1)
                    {
                        png.DpiX = (float)(BinaryPrimitives.ReadUInt32BigEndian(body) * 0.0254);
                        png.DpiY = (float)(BinaryPrimitives.ReadUInt32BigEndian(body[4..]) * 0.0254);
                    }
                }
                else if (type.SequenceEqual("iCCP"u8) && !options.IgnoreMetadata)
                {
                    png.Icc = ReadIccp(body);
                }
                else if (type.SequenceEqual("eXIf"u8) && !options.IgnoreMetadata)
                {
                    png.Exif = body.ToArray();
                }
                else if (type.SequenceEqual("iTXt"u8) && !options.IgnoreMetadata)
                {
                    png.Xmp ??= ReadXmp(body);
                }
                else if (type.SequenceEqual("acTL"u8))
                {
                    if (length >= 8 && !seenIdat && png.Animation is null)
                    {
                        int numFrames = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body), int.MaxValue);
                        int numPlays = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                        if (numFrames > 0)
                        {
                            png.Animation = new PngAnimation { NumFrames = numFrames, NumPlays = numPlays };
                        }
                    }
                }
                else if (type.SequenceEqual("fcTL"u8))
                {
                    if (png.Animation is null || length < 26)
                    {
                        animationValid = false;
                        continue;
                    }

                    currentFrame = new ApngFrame
                    {
                        Sequence = (int)BinaryPrimitives.ReadUInt32BigEndian(body),
                        Width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue),
                        Height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[8..]), int.MaxValue),
                        X = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[12..]), int.MaxValue),
                        Y = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[16..]), int.MaxValue),
                        DelayNum = BinaryPrimitives.ReadUInt16BigEndian(body[20..]),
                        DelayDen = BinaryPrimitives.ReadUInt16BigEndian(body[22..]),
                        DisposeOp = body[24],
                        BlendOp = body[25],
                        IsDefaultImage = !seenIdat,
                    };
                    if (currentFrame.DisposeOp > 2 || currentFrame.BlendOp > 1)
                    {
                        animationValid = false;
                    }

                    png.Animation.Frames.Add(currentFrame);
                }
                else if (type.SequenceEqual("fdAT"u8))
                {
                    if (currentFrame is null || currentFrame.IsDefaultImage || length < 4)
                    {
                        animationValid = false;
                        continue;
                    }

                    currentFrame.Parts.Add((payload, (int)length));
                }
                else if ((type[0] & 0x20) == 0)
                {
                    throw new NotSupportedException($"Unsupported critical PNG chunk '{Encoding.ASCII.GetString(type)}'.");
                }
            }

            if (!seenIhdr)
            {
                throw Error("Missing IHDR chunk.");
            }

            if (!metadataOnly && !seenIdat)
            {
                throw Error("Missing IDAT chunk.");
            }

            _ = seenEnd;  // a missing IEND is tolerated

            if (png.Animation is { } anim)
            {
                // Like libpng's APNG patch: an inconsistent animation falls back to the static image.
                if (!animationValid || anim.Frames.Count == 0 ||
                    (anim.Frames[0].IsDefaultImage && (anim.Frames[0].X != 0 || anim.Frames[0].Y != 0 ||
                        anim.Frames[0].Width != png.Width || anim.Frames[0].Height != png.Height)))
                {
                    png.Animation = null;
                }
            }

            return png;
        }

        private void ReadHeader(ReadOnlySpan<byte> body)
        {
            if (body.Length < 13)
            {
                throw Error("IHDR chunk is too small.");
            }

            uint w = BinaryPrimitives.ReadUInt32BigEndian(body);
            uint h = BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
            if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
            {
                throw Error("Invalid image dimensions.");
            }

            Width = (int)w;
            Height = (int)h;
            BitDepth = body[8];
            Color = (PngColor)body[9];
            bool valid = Color switch
            {
                PngColor.Gray => BitDepth is 1 or 2 or 4 or 8 or 16,
                PngColor.Palette => BitDepth is 1 or 2 or 4 or 8,
                PngColor.Rgb or PngColor.GrayAlpha or PngColor.Rgba => BitDepth is 8 or 16,
                _ => false,
            };
            if (!valid)
            {
                throw Error($"Invalid color type {(int)Color} / bit depth {BitDepth} combination.");
            }

            if (body[10] != 0 || body[11] != 0 || body[12] > 1)
            {
                throw Error("Invalid compression, filter or interlace method.");
            }

            Interlaced = body[12] == 1;
        }

        private static byte[]? ReadIccp(ReadOnlySpan<byte> body)
        {
            int nul = body.IndexOf((byte)0);
            if (nul < 1 || nul > 79 || nul + 2 > body.Length || body[nul + 1] != 0)
            {
                return null;
            }

            try
            {
                byte[] compressed = body[(nul + 2)..].ToArray();
                return Inflater.InflateZlib(compressed, 0, compressed.Length, 4096, 64 << 20);
            }
            catch (InvalidDataStreamException)
            {
                return null;  // a broken profile is ignored, like libpng does
            }
        }

        private static byte[]? ReadXmp(ReadOnlySpan<byte> body)
        {
            int nul = body.IndexOf((byte)0);
            if (nul < 0 || !body[..nul].SequenceEqual(Encoding.ASCII.GetBytes(PngConstants.XmpKeyword)) || nul + 3 > body.Length)
            {
                return null;
            }

            bool compressed = body[nul + 1] != 0;
            ReadOnlySpan<byte> rest = body[(nul + 3)..];
            int langEnd = rest.IndexOf((byte)0);
            if (langEnd < 0)
            {
                return null;
            }

            rest = rest[(langEnd + 1)..];
            int keyEnd = rest.IndexOf((byte)0);
            if (keyEnd < 0)
            {
                return null;
            }

            rest = rest[(keyEnd + 1)..];
            if (!compressed)
            {
                return rest.ToArray();
            }

            try
            {
                byte[] data = rest.ToArray();
                return Inflater.InflateZlib(data, 0, data.Length, 4096, 64 << 20);
            }
            catch (InvalidDataStreamException)
            {
                return null;
            }
        }
    }
}
