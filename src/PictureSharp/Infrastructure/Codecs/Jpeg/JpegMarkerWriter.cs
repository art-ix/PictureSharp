using PictureSharp.Domain.Metadata;
using PictureSharp.Infrastructure.Metadata;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed class JpegMarkerWriter(Stream output)
{
    public void Marker(int marker)
    {
        output.WriteByte(255);
        output.WriteByte((byte)marker);
    }

    private void Word(int value)
    {
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    private void Segment(int marker, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload)
    {
        Marker(marker);
        Word(prefix.Length + payload.Length + 2);
        output.Write(prefix);
        output.Write(payload);
    }

    public static void ValidateMetadata(ImageMetadata metadata, MetadataKinds kinds)
    {
        if (kinds.HasFlag(MetadataKinds.Exif) && metadata.ExifData is { Length: > 65527 })
            throw new ArgumentException("JPEG EXIF data must fit in one APP1 segment (65527 bytes).", nameof(metadata));
        if (kinds.HasFlag(MetadataKinds.Xmp) && metadata.XmpData is { Length: > 65504 })
            throw new ArgumentException("JPEG XMP data must fit in one APP1 segment (65504 bytes).", nameof(metadata));
        if (kinds.HasFlag(MetadataKinds.Icc) && metadata.IccData is { Length: > 65519 * 255 })
            throw new ArgumentException("JPEG ICC data must fit in 255 APP2 segments.", nameof(metadata));
    }

    // port: libjpeg-turbo jcmarker.c emit_app0, jpeg_write_m_header; Pillow JpegEncode.c metadata order
    public void Header(Image image, MetadataKinds kinds)
    {
        Marker(0xD8);
        int x = (int)Math.Clamp(Math.Round(image.Metadata.HorizontalResolution), 1, 65535);
        int y = (int)Math.Clamp(Math.Round(image.Metadata.VerticalResolution), 1, 65535);
        Span<byte> jfif = stackalloc byte[14];
        "JFIF\0"u8.CopyTo(jfif);
        jfif[5] = 1;
        jfif[6] = 1;
        jfif[7] = 1;
        jfif[8] = (byte)(x >> 8);
        jfif[9] = (byte)x;
        jfif[10] = (byte)(y >> 8);
        jfif[11] = (byte)y;
        jfif[12] = jfif[13] = 0;
        Segment(0xE0, [], jfif);
        ImageMetadata m = image.Metadata;
        if (MetadataSelection.Select(m.ExifData, kinds, MetadataKinds.Exif) is { } exif)
            Segment(0xE1, "Exif\0\0"u8, exif);
        if (MetadataSelection.Select(m.IccData, kinds, MetadataKinds.Icc) is { } icc)
        {
            int count = (icc.Length + 65518) / 65519;
            Span<byte> prefix = stackalloc byte[14];
            "ICC_PROFILE\0"u8.CopyTo(prefix);
            prefix[13] = (byte)count;
            for (int i = 0; i < count; i++)
            {
                prefix[12] = (byte)(i + 1);
                Segment(0xE2, prefix, icc.AsSpan(i * 65519, Math.Min(65519, icc.Length - i * 65519)));
            }
        }

        if (MetadataSelection.Select(m.XmpData, kinds, MetadataKinds.Xmp) is { } xmp)
            Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0"u8, xmp);
    }

    // port: libjpeg-turbo jcmarker.c emit_dqt, emit_sof
    public void Frame(Image image, JpegEncoderComponent[] components, byte[][] tables, bool progressive)
    {
        for (int i = 0; i < tables.Length; i++)
        {
            Marker(0xDB);
            Word(67);
            output.WriteByte((byte)i);
            for (int k = 0; k < 64; k++) output.WriteByte(tables[i][JpegTables.NaturalOrder[k]]);
        }

        Marker(progressive ? 0xC2 : 0xC0);
        Word(8 + 3 * components.Length);
        output.WriteByte(8);
        Word(image.Height);
        Word(image.Width);
        output.WriteByte((byte)components.Length);
        for (int c = 0; c < components.Length; c++)
        {
            output.WriteByte((byte)(c + 1));
            output.WriteByte((byte)(components[c].Horizontal * 16 + components[c].Vertical));
            output.WriteByte((byte)(c == 0 ? 0 : 1));
        }
    }

    // port: libjpeg-turbo jcmarker.c emit_dht
    public void Huffman(int index, JpegHuffmanEncoder table)
    {
        Marker(0xC4);
        Word(19 + table.Values.Length);
        output.WriteByte((byte)((index % 2) * 16 + index / 2));
        output.Write(table.Counts);
        output.Write(table.Values);
    }

    // port: libjpeg-turbo jcmarker.c emit_dri
    public void RestartInterval(int interval)
    {
        Marker(0xDD);
        Word(4);
        Word(interval);
    }

    // port: libjpeg-turbo jcmarker.c emit_sos
    public void Scan(JpegEncoderScan scan, int components, bool progressive)
    {
        int count = scan.Interleaved ? components : 1;
        Marker(0xDA);
        Word(6 + 2 * count);
        output.WriteByte((byte)count);
        for (int i = 0; i < count; i++)
        {
            int c = scan.Interleaved ? i : scan.Component;
            int table = c == 0 ? 0 : 1;
            output.WriteByte((byte)(c + 1));
            int dc = !progressive || (scan.Start == 0 && scan.High == 0) ? table : 0;
            int ac = !progressive || scan.Start != 0 ? table : 0;
            output.WriteByte((byte)(dc * 16 + ac));
        }

        output.WriteByte((byte)scan.Start);
        output.WriteByte((byte)scan.End);
        output.WriteByte((byte)(scan.High * 16 + scan.Low));
    }
}
