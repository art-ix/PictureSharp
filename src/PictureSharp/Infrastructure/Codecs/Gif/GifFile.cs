using PictureSharp.Application.Decoding;
using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Gif;

/// <summary>One image (frame) of a GIF file: its descriptor, graphic control values and the position of its LZW data.</summary>
internal sealed class GifFrameInfo
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Interlaced { get; init; }
    public Rgba32[]? LocalColorTable { get; init; }
    public int DelayCentiseconds { get; init; }
    public GifDisposal Disposal { get; init; }

    /// <summary>Palette index drawn as transparent, or -1.</summary>
    public int TransparentIndex { get; init; } = -1;

    public int MinCodeSize { get; init; }

    /// <summary>Offset of the first data sub-block (its length byte).</summary>
    public int DataOffset { get; init; }
}

/// <summary>
/// The block structure of a GIF file. Parsing is tolerant like browsers and Pillow: a file that ends early, or a
/// block that is not an image, extension or trailer, ends the stream after the frames read so far.
/// </summary>
internal sealed class GifFile
{
    private GifFile()
    {
    }

    /// <summary>Logical screen (canvas) width.</summary>
    public int Width { get; private set; }

    /// <summary>Logical screen (canvas) height.</summary>
    public int Height { get; private set; }

    public Rgba32[]? GlobalColorTable { get; private set; }

    public int BackgroundIndex { get; private set; }

    public List<GifFrameInfo> Frames { get; } = [];

    /// <summary>Repeat count from the first NETSCAPE2.0/ANIMEXTS1.0 extension (0 = forever), or null when absent.</summary>
    public int? Repetitions { get; private set; }

    public byte[]? Xmp { get; private set; }

    public byte[]? Icc { get; private set; }

    /// <summary>Whether the decoded canvas can contain transparent pixels.</summary>
    public bool HasTransparency
    {
        get
        {
            GifFrameInfo first = Frames[0];
            if (first.X > 0 || first.Y > 0 || first.X + first.Width < Width || first.Y + first.Height < Height)
            {
                return true;
            }

            foreach (GifFrameInfo frame in Frames)
            {
                if (frame.TransparentIndex >= 0 || frame.Disposal != GifDisposal.None)
                {
                    return true;
                }
            }

            return false;
        }
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.Gif, message);

    public static GifFile Parse(ReadOnlySpan<byte> data, DecoderOptions options, bool readMetadata)
    {
        if (data.Length < 13 || !GifFormatDetector.Instance.IsSupported(data))
        {
            throw Error("Not a GIF file.");
        }

        var gif = new GifFile
        {
            Width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),
            BackgroundIndex = data[11],
        };

        int pos = 13;
        int flags = data[10];
        if ((flags & 0x80) != 0)
        {
            gif.GlobalColorTable = ReadColorTable(data, ref pos, flags & 7) ?? throw Error("The global color table is truncated.");
        }

        // Graphic control values apply to the next image only.
        int delay = 0, transparent = -1;
        GifDisposal disposal = GifDisposal.None;
        bool done = false;
        while (!done && pos < data.Length)
        {
            switch (data[pos++])
            {
                case GifConstants.ExtensionIntroducer:
                    if (pos >= data.Length)
                    {
                        done = true;
                        break;
                    }

                    byte label = data[pos++];
                    int first = pos;
                    if (label == GifConstants.GraphicControlLabel && first + 5 <= data.Length && data[first] == 4)
                    {
                        // port: examples/gifdec.c GIFReadGraphicsExtension
                        int packed = data[first + 1];
                        int method = (packed >> 2) & 7;
                        disposal = method is 2 or 3 ? (GifDisposal)method : GifDisposal.None;
                        delay = BinaryPrimitives.ReadUInt16LittleEndian(data[(first + 2)..]);
                        transparent = (packed & 1) != 0 ? data[first + 4] : -1;
                    }
                    else if (label == GifConstants.ApplicationLabel && first + 12 <= data.Length && data[first] == 11)
                    {
                        gif.ReadApplicationExtension(data, data.Slice(first + 1, 11), first + 12, readMetadata ? options : null);
                    }

                    done = !SkipSubBlocks(data, ref pos);
                    break;

                case GifConstants.ImageSeparator:
                    GifFrameInfo? frame = ReadImage(data, ref pos, delay, disposal, transparent, out bool complete);
                    if (frame is not null)
                    {
                        options.ValidateFrameCount(ImageFormat.Gif, gif.Frames.Count + 1L);
                        gif.Frames.Add(frame);
                    }

                    (delay, disposal, transparent) = (0, GifDisposal.None, -1);
                    done = !complete;
                    break;

                default:
                    // The trailer, or garbage that ends the stream.
                    done = true;
                    break;
            }
        }

        if (gif.Frames.Count == 0)
        {
            throw Error("The file contains no image.");
        }

        // port: examples/gif2webp.c (broken headers that report a 0 x 0 screen use the first frame's size)
        if (gif.Width == 0 || gif.Height == 0)
        {
            GifFrameInfo f = gif.Frames[0];
            (f.X, f.Y) = (0, 0);
            (gif.Width, gif.Height) = (f.Width, f.Height);
            if (gif.Width == 0 || gif.Height == 0)
            {
                throw Error("Invalid canvas size.");
            }
        }

        return gif;
    }

    private static GifFrameInfo? ReadImage(ReadOnlySpan<byte> data, ref int pos, int delay, GifDisposal disposal, int transparent, out bool complete)
    {
        complete = false;
        if (pos + 9 > data.Length)
        {
            return null;
        }

        int x = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
        int y = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 4)..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 6)..]);
        int flags = data[pos + 8];
        pos += 9;
        Rgba32[]? local = null;
        if ((flags & 0x80) != 0 && (local = ReadColorTable(data, ref pos, flags & 7)) is null)
        {
            return null;
        }

        if (pos >= data.Length)
        {
            return null;
        }

        var frame = new GifFrameInfo
        {
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Interlaced = (flags & 0x40) != 0,
            LocalColorTable = local,
            DelayCentiseconds = delay,
            Disposal = disposal,
            TransparentIndex = transparent,
            MinCodeSize = data[pos],
            DataOffset = pos + 1,
        };
        pos++;
        complete = SkipSubBlocks(data, ref pos);
        return frame;
    }

    /// <summary>Reads loop counts, and ICC/XMP blocks when <paramref name="metadataOptions"/> is given.</summary>
    private void ReadApplicationExtension(ReadOnlySpan<byte> data, ReadOnlySpan<byte> identifier, int pos, DecoderOptions? metadataOptions)
    {
        if (identifier.SequenceEqual(GifConstants.Netscape) || identifier.SequenceEqual(GifConstants.AnimExts))
        {
            // port: examples/gifdec.c GIFReadLoopCount (the first loop extension wins, as in anim_util.c)
            if (Repetitions is null && pos + 4 <= data.Length && data[pos] >= 3 && data[pos + 1] == 1)
            {
                Repetitions = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            }

            return;
        }

        if (metadataOptions is null)
        {
            return;
        }

        // port: examples/gifdec.c GIFReadMetadata (only the first block of each kind is kept)
        bool isXmp = Xmp is null && identifier.SequenceEqual(GifConstants.XmpIdentifier);
        bool isIcc = Icc is null && identifier.SequenceEqual(GifConstants.IccIdentifier);
        if (!isXmp && !isIcc)
        {
            return;
        }

        int start = pos;
        var payload = new List<byte>();
        while (pos < data.Length && data[pos] != 0)
        {
            int size = data[pos];
            if (pos + 1 + size > data.Length)
            {
                return;
            }

            if (isIcc)
            {
                payload.AddRange(data.Slice(pos + 1, size));
            }

            pos += 1 + size;
        }

        if (isXmp)
        {
            // In XMP each sub-block's length byte is part of the packet, followed by a 257-byte "magic trailer".
            int length = pos - start;
            length = length > GifConstants.XmpTrailerLength ? length - GifConstants.XmpTrailerLength : length;
            metadataOptions.AddMetadata(ImageFormat.Gif, (Icc?.Length) ?? 0, length);
            Xmp = data.Slice(start, length).ToArray();
        }
        else
        {
            metadataOptions.AddMetadata(ImageFormat.Gif, (Xmp?.Length) ?? 0, payload.Count);
            Icc = [.. payload];
        }
    }

    private static Rgba32[]? ReadColorTable(ReadOnlySpan<byte> data, ref int pos, int sizeBits)
    {
        int count = 2 << sizeBits;
        if (pos + (3 * count) > data.Length)
        {
            return null;
        }

        var table = new Rgba32[count];
        for (int i = 0; i < count; i++)
        {
            table[i] = new Rgba32(data[pos], data[pos + 1], data[pos + 2]);
            pos += 3;
        }

        return table;
    }

    /// <summary>Skips a chain of data sub-blocks; returns false when the data ends before the terminator.</summary>
    private static bool SkipSubBlocks(ReadOnlySpan<byte> data, ref int pos)
    {
        while (pos < data.Length)
        {
            int size = data[pos];
            pos += size + 1;
            if (size == 0)
            {
                return true;
            }
        }

        pos = data.Length;
        return false;
    }
}
