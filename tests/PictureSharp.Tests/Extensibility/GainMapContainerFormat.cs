using System.Buffers.Binary;
using System.Drawing;
using System.Text;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// A test container exercising the 0.7.0 plugin SPI: a base image, a gain map image with ISO 21496-1 or Apple metadata,
/// and an XMP block read and rewritten without pixels. Layout: "GMAP", then sections of a tag byte, a big-endian
/// length and the data.
/// </summary>
public sealed class GainMapContainerFormat(bool editableMetadata = true) : IImageFormatModule
{
    public const byte Base = 1, Map = 2, Iso = 3, MapIcc = 4, Exif = 5, MapXmp = 6, Xmp = 7, Channels = 8;

    public static ImageFormat Format { get; } = ImageFormat.Create("GMAP", "image/x-test-gain-map", ".gmap");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("GMAP"u8);

    public IImageFormatDecoder? Decoder { get; } = new GainMapDecoder();

    public IImageFormatMetadataCodec? MetadataCodec { get; } = editableMetadata ? new EditableMetadataCodec() : new ReadOnlyMetadataCodec();

    public static byte[] Write(IEnumerable<(byte Tag, byte[] Data)> sections)
    {
        var output = new MemoryStream();
        output.Write("GMAP"u8);
        Span<byte> length = stackalloc byte[4];
        foreach ((byte tag, byte[] data) in sections)
        {
            output.WriteByte(tag);
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            output.Write(length);
            output.Write(data);
        }

        return output.ToArray();
    }

    public static Dictionary<byte, ReadOnlyMemory<byte>> Read(ReadOnlyMemory<byte> data)
    {
        var sections = new Dictionary<byte, ReadOnlyMemory<byte>>();
        if (!data.Span.StartsWith("GMAP"u8))
        {
            throw new ImageFormatException(Format, "Missing signature.");
        }

        int position = 4;
        while (position < data.Length)
        {
            byte tag = data.Span[position];
            int length = BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(position + 1, 4));
            sections[tag] = data.Slice(position + 5, length);
            position += 5 + length;
        }

        return sections;
    }

    /// <summary>The same base, map and metadata as a JPEG file with a gain map, repackaged.</summary>
    public static byte[] FromJpeg(byte[] jpeg)
    {
        (int mapStart, int mapLength) = MapLocation(jpeg);
        byte[] map = jpeg.AsSpan(mapStart, mapLength).ToArray();
        List<(byte, byte[])> sections = [(Base, jpeg.AsSpan(0, mapStart).ToArray()), (Map, map)];
        if (Segment(map, 0xE2, "urn:iso:std:iso:ts:21496:-1\0"u8) is { } iso)
        {
            sections.Add((Iso, iso));
        }

        if (Segment(map, 0xE2, "ICC_PROFILE\0"u8) is { } icc)
        {
            sections.Add((MapIcc, icc[2..]));   // a single chunk: skip the sequence number and count
        }

        if (Segment(jpeg, 0xE1, "Exif\0\0"u8) is { } exif)
        {
            sections.Add((Exif, exif));
        }

        if (Segment(map, 0xE1, "http://ns.adobe.com/xap/1.0/\0"u8) is { } xmp)
        {
            sections.Add((MapXmp, xmp));
        }

        sections.Add((Channels, [Components(map)]));
        return Write(sections);
    }

    /// <summary>The body of the first APPn segment of <paramref name="marker"/> starting with <paramref name="prefix"/>.</summary>
    private static byte[]? Segment(ReadOnlySpan<byte> jpeg, byte marker, ReadOnlySpan<byte> prefix)
    {
        int position = 2;
        while (position + 4 <= jpeg.Length && jpeg[position] == 0xFF && jpeg[position + 1] != 0xDA)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
            ReadOnlySpan<byte> body = jpeg.Slice(position + 4, length - 2);
            if (jpeg[position + 1] == marker && body.StartsWith(prefix))
            {
                return body[prefix.Length..].ToArray();
            }

            position += 2 + length;
        }

        return null;
    }

    private static byte Components(ReadOnlySpan<byte> jpeg)
    {
        int position = 2;
        while (jpeg[position + 1] is not (0xC0 or 0xC1 or 0xC2))
        {
            position += 2 + BinaryPrimitives.ReadUInt16BigEndian(jpeg[(position + 2)..]);
        }

        return jpeg[position + 9];
    }

    // The first secondary image of the MPF directory (offsets relative to the MPF TIFF header).
    private static (int Start, int Length) MapLocation(byte[] jpeg)
    {
        int position = 2;
        while (true)
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(position + 2));
            if (jpeg[position + 1] == 0xE2 && jpeg.AsSpan(position + 4).StartsWith("MPF\0"u8))
            {
                int tiff = position + 8;
                bool big = jpeg[tiff] == 'M';
                int ifd = (int)Read32(jpeg.AsSpan(tiff + 4), big);
                int count = Read16(jpeg.AsSpan(tiff + ifd), big);
                for (int i = 0; i < count; i++)
                {
                    ReadOnlySpan<byte> entry = jpeg.AsSpan(tiff + ifd + 2 + (12 * i), 12);
                    if (Read16(entry, big) == 0xB002)
                    {
                        int list = tiff + (int)Read32(entry[8..], big);
                        ReadOnlySpan<byte> second = jpeg.AsSpan(list + 16, 16);
                        return (tiff + (int)Read32(second[8..], big), (int)Read32(second[4..], big));
                    }
                }
            }

            position += 2 + length;
        }
    }

    private static int Read16(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint Read32(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    private sealed class GainMapDecoder : IImageFormatDecoder
    {
        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
        {
            Dictionary<byte, ReadOnlyMemory<byte>> sections = Read(data);
            ImageInfo info = Image.Identify(sections[Base]);
            return info with { Format = Format, ContainerItemCount = sections.ContainsKey(Map) ? 2 : 1 };
        }

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            Dictionary<byte, ReadOnlyMemory<byte>> sections = Read(data);
            Image image = context.DecodeEmbedded(sections[Base]);
            if (sections.TryGetValue(Xmp, out ReadOnlyMemory<byte> xmp))
            {
                image.Metadata.Xmp = xmp.ToArray();
            }

            if (context.Options.LoadGainMap && sections.TryGetValue(Map, out ReadOnlyMemory<byte> map))
            {
                ImageInfo mapInfo = Image.Identify(map);
                var size = new Size(mapInfo.Width, mapInfo.Height);
                try
                {
                    GainMapMetadata metadata = sections.TryGetValue(Iso, out ReadOnlyMemory<byte> iso)
                        ? GainMapMetadata.ParseIso(iso.Span, sections[Channels].Span[0], sections.GetValueOrDefault(MapIcc).Span)
                        : GainMapMetadata.ParseApple(sections.GetValueOrDefault(Exif).Span, sections.GetValueOrDefault(MapXmp).Span);
                    context.AttachGainMap(metadata, new JpegMap(map.ToArray(), size));
                }
                catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
                {
                    context.DeclareGainMap(sections.ContainsKey(Iso) ? HdrGainMapKinds.Iso21496 : HdrGainMapKinds.Apple, ex.Message,
                        corrupt: ex is ImageFormatException);
                }
            }

            return image;
        }
    }

    private sealed class JpegMap(byte[] data, Size size) : GainMapImage(size)
    {
        public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
            Image.Load(data, new DecoderOptions { IgnoreMetadata = true, MaxDegreeOfParallelism = maxDegreeOfParallelism }, cancellationToken);
    }

    private class ReadOnlyMetadataCodec : IImageFormatMetadataCodec
    {
        public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            var metadata = new ImageMetadata();
            if (GainMapContainerFormat.Read(data).TryGetValue(Xmp, out ReadOnlyMemory<byte> xmp))
            {
                context.ReserveMetadata(xmp.Length);
                metadata.Xmp = xmp.ToArray();
            }

            return metadata;
        }
    }

    private sealed class EditableMetadataCodec : ReadOnlyMetadataCodec, IImageFormatMetadataCodec
    {
        public static int Rewrites;

        public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, ImageDecodeContext context)
        {
            Interlocked.Increment(ref Rewrites);
            List<(byte, byte[])> sections = [.. GainMapContainerFormat.Read(data).Where(s => s.Key != Xmp).Select(s => (s.Key, s.Value.ToArray()))];
            if (metadata.Xmp is { } xmp)
            {
                sections.Add((Xmp, xmp));
            }

            return Write(sections);
        }
    }

    public static int RewriteCount => EditableMetadataCodec.Rewrites;

    internal static string Text(byte[]? data) => data is null ? "" : Encoding.UTF8.GetString(data);
}
