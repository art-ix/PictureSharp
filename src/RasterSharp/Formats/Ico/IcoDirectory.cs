using System.Buffers.Binary;
using RasterSharp.Formats.Png;

namespace RasterSharp.Formats.Ico;

internal sealed class IcoDirectory
{
    internal sealed record Entry(IcoEntryInfo Info, ReadOnlyMemory<byte> Data);
    public required Entry[] Entries { get; init; }

    internal static ImageFormatException Error(string message) => new(ImageFormat.Ico, message);

    public static IcoDirectory Parse(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        try
        {
            return ParseCore(data, options);
        }
        catch (Exception ex) when (ImageCodec.IsCorruptDataException(ex))
        {
            throw ImageCodec.CorruptData(ImageFormat.Ico, ex);
        }
    }

    private static IcoDirectory ParseCore(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        ReadOnlySpan<byte> file = data.Span;
        if (file.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(file) != 0)
        {
            throw Error("Invalid ICO header.");
        }

        int type = BinaryPrimitives.ReadUInt16LittleEndian(file[2..]);
        if (type == 2)
        {
            throw new NotSupportedException("CUR files are not supported.");
        }

        if (type != 1)
        {
            throw Error("Invalid ICO type.");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(file[4..]);
        int end = 6 + (16 * count);
        if (count == 0 || file.Length < end)
        {
            throw Error("Empty or truncated ICO directory.");
        }

        var entries = new Entry[count];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> dir = file.Slice(6 + (16 * i), 16);
            int width = dir[0] == 0 ? 256 : dir[0], height = dir[1] == 0 ? 256 : dir[1];
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(dir[8..]);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(dir[12..]);
            int planes = BinaryPrimitives.ReadUInt16LittleEndian(dir[4..]);
            int declaredBits = BinaryPrimitives.ReadUInt16LittleEndian(dir[6..]);
            if (dir[3] != 0 || planes > 1 || offset < end || length < 4 || (ulong)offset + length > (ulong)file.Length)
            {
                throw Error("Invalid ICO entry bounds or fields.");
            }

            ReadOnlyMemory<byte> payload = data.Slice((int)offset, (int)length);
            bool png = PngFormatDetector.Instance.IsSupported(payload.Span);
            int bits;
            bool supported;
            if (png)
            {
                // Check IHDR dimensions before parsing metadata or allocating embedded decoder buffers.
                ReadOnlySpan<byte> bytes = payload.Span;
                if (bytes.Length < 33 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
                    || BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != 13
                    || BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) != width
                    || BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]) != height)
                {
                    throw Error("ICO directory and PNG dimensions disagree.");
                }

                ImageInfo info = PngDecoder.Instance.Identify(payload, options with { IgnoreMetadata = true });
                int channels = bytes[25] switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                bits = bytes[24] * channels;
                supported = !info.IsAnimated && info.FrameCount == 1;
            }
            else
            {
                IcoDib.Header header = IcoDib.Parse(payload.Span, width, height);
                bits = header.Bits;
                supported = header.Supported;
            }

            // PNG writers commonly advertise 32-bit display depth even for RGB or palette payloads.
            if (!png && declaredBits != 0 && declaredBits != bits)
            {
                throw Error("ICO directory and image bit depths disagree.");
            }

            entries[i] = new(new(i, width, height, bits, png ? IcoEncoding.Png : IcoEncoding.Dib, supported), payload);
        }

        return new() { Entries = entries };
    }

    public Entry Select(DecoderOptions options)
    {
        if (options.IcoIndex is int index)
        {
            if (index >= Entries.Length)
            {
                throw Error("ICO index is outside the directory.");
            }

            Entry chosen = Entries[index];
            if (!chosen.Info.IsSupported)
            {
                throw new NotSupportedException("The selected ICO variant is not supported.");
            }

            return chosen;
        }

        Entry? best = null;
        long bestDistance = long.MaxValue;
        foreach (Entry entry in Entries)
        {
            if (!entry.Info.IsSupported)
            {
                continue;
            }

            IcoEntryInfo info = entry.Info;
            long distance = options.IcoPreferredSize is { } size
                ? Math.Abs(info.Width - size.Width) + Math.Abs(info.Height - size.Height) : 0;
            if (best is null || distance < bestDistance || (distance == bestDistance && Better(info, best.Info)))
            {
                best = entry;
                bestDistance = distance;
            }
        }

        return best ?? throw new NotSupportedException("No supported ICO variants.");
    }

    private static bool Better(IcoEntryInfo a, IcoEntryInfo b) =>
        a.Width * a.Height > b.Width * b.Height || (a.Width * a.Height == b.Width * b.Height && a.BitsPerPixel > b.BitsPerPixel);
}
