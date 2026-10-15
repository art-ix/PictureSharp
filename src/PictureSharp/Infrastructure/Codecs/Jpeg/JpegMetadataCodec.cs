using System.Buffers.Binary;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Infrastructure.Metadata;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

internal sealed class JpegMetadataCodec : IImageMetadataCodec
{
    private readonly record struct Segment(int Start, int Length, int Body, int BodyLength, int Marker, MetadataKinds Kind);
    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> XmpPrefix => "http://ns.adobe.com/xap/1.0/\0"u8;
    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;

    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        List<Segment> segments = Parse(data.Span, context.CancellationToken);
        var metadata = new ImageMetadata();
        var icc = new SortedDictionary<int, byte[]>();
        int iccCount = 0;
        foreach (Segment segment in segments)
        {
            ReadOnlySpan<byte> body = data.Span.Slice(segment.Body, segment.BodyLength);
            switch (segment.Kind)
            {
                case MetadataKinds.Exif:
                    byte[] exif = MetadataContainer.Copy(body[ExifPrefix.Length..], ImageFormat.Jpeg, context);
                    metadata.ExifData ??= exif;
                    break;
                case MetadataKinds.Xmp:
                    byte[] xmp = MetadataContainer.Copy(body[XmpPrefix.Length..], ImageFormat.Jpeg, context);
                    metadata.XmpData ??= xmp;
                    break;
                case MetadataKinds.Icc:
                    if (body.Length < 14 || body[12] == 0 || body[13] == 0 || body[12] > body[13]
                        || iccCount != 0 && iccCount != body[13] || icc.ContainsKey(body[12])) throw Error("Invalid ICC segment sequence.");
                    iccCount = body[13];
                    icc.Add(body[12], MetadataContainer.Copy(body[14..], ImageFormat.Jpeg, context));
                    break;
            }
        }
        if (iccCount != icc.Count) throw Error("Incomplete ICC profile.");
        if (iccCount > 0) metadata.IccData = icc.Values.SelectMany(b => b).ToArray();
        JpegFile file = JpegFile.Read(data.Span, context.Options with { IgnoreMetadata = true }, headerOnly: true,
            cancellationToken: context.CancellationToken);
        context.Budget.ReserveImage(ImageFormat.Jpeg, file.Frame!.Width, file.Frame.Height);
        (metadata.HorizontalResolution, metadata.VerticalResolution) = file.Resolution();
        return metadata;
    }

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata metadata, DecodeContext context)
    {
        var segments = Parse(data.Span, context.CancellationToken);
        var original = Read(data, new(context.Options, context.CancellationToken));
        if (MetadataContainer.Same(original, metadata)) return data.ToArray();
        bool resolution = metadata.HorizontalResolution != original.HorizontalResolution || metadata.VerticalResolution != original.VerticalResolution;
        using var output = new PooledBufferStream();
        output.Write(new byte[] { 0xff, 0xd8 });
        if (resolution)
        {
            if (metadata.HorizontalResolution > 65535 || metadata.VerticalResolution > 65535) throw new ArgumentOutOfRangeException(nameof(metadata), "JPEG JFIF resolution must fit in 16 bits.");
            Segment existing = segments.FirstOrDefault(s => s.Marker == 0xe0 && s.BodyLength >= 14
                && data.Span.Slice(s.Body, s.BodyLength).StartsWith("JFIF\0"u8));
            byte[] jfif = existing.BodyLength >= 14 ? data.Span.Slice(existing.Body, existing.BodyLength).ToArray() : new byte[14];
            if (existing.BodyLength == 0)
            {
                "JFIF\0"u8.CopyTo(jfif);
                jfif[5] = 1; jfif[6] = 2;
            }
            jfif[7] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(jfif.AsSpan(8), (ushort)Math.Max(1, Math.Round(metadata.HorizontalResolution)));
            BinaryPrimitives.WriteUInt16BigEndian(jfif.AsSpan(10), (ushort)Math.Max(1, Math.Round(metadata.VerticalResolution)));
            WriteSegment(output, 0xe0, jfif, context.CancellationToken);
        }
        // JFIF/Adobe headers precede the replacement profiles, preserving their conventional ordering.
        bool written = false;
        foreach (Segment segment in segments)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (segment.Kind != MetadataKinds.None) continue;
            ReadOnlySpan<byte> body = data.Span.Slice(segment.Body, segment.BodyLength);
            if (resolution && segment.Marker == 0xe0 && body.StartsWith("JFIF\0"u8)) continue;
            if (!written && segment.Marker is not (0xe0 or 0xee))
            {
                WriteProfiles(output, metadata, context.CancellationToken);
                written = true;
            }
            MetadataContainer.Write(output, data.Span.Slice(segment.Start, segment.Length), context.CancellationToken);
        }
        return MetadataContainer.ToArray(output);
    }

    private static void WriteProfiles(Stream output, ImageMetadata metadata, CancellationToken token)
    {
        if (metadata.ExifData is { Length: > 0 } exif)
        {
            if (exif.Length > 65533 - ExifPrefix.Length) throw new NotSupportedException("JPEG EXIF must fit in one APP1 segment.");
            WriteSegment(output, 0xe1, [.. ExifPrefix, .. exif], token);
        }
        if (metadata.XmpData is { Length: > 0 } xmp)
        {
            if (xmp.Length > 65533 - XmpPrefix.Length) throw new NotSupportedException("JPEG XMP must fit in one APP1 segment.");
            WriteSegment(output, 0xe1, [.. XmpPrefix, .. xmp], token);
        }
        if (metadata.IccData is not { Length: > 0 } icc) return;
        const int maximum = 65519;
        int count = (int)(((long)icc.Length + maximum - 1) / maximum);
        if (count > 255) throw new NotSupportedException("The ICC profile exceeds JPEG's 255-segment limit.");
        for (int i = 0; i < count; i++)
        {
            int length = Math.Min(maximum, icc.Length - (i * maximum));
            WriteSegment(output, 0xe2, [.. IccPrefix, (byte)(i + 1), (byte)count, .. icc.AsSpan(i * maximum, length)], token);
        }
    }

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> body, CancellationToken token)
    {
        if (body.Length > 65533) throw new NotSupportedException("JPEG EXIF/XMP must fit in one APP1 segment.");
        Span<byte> header = stackalloc byte[4];
        header[0] = 0xff; header[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(body.Length + 2));
        output.Write(header);
        MetadataContainer.Write(output, body, token);
    }

    private static List<Segment> Parse(ReadOnlySpan<byte> data, CancellationToken token)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8) throw Error("Missing SOI marker.");
        var segments = new List<Segment>();
        int position = 2;
        bool scan = false, sawScan = false;
        while (position < data.Length)
        {
            token.ThrowIfCancellationRequested();
            if (scan)
            {
                int start = position;
                while (position < data.Length)
                {
                    if ((position & 65535) == 0) token.ThrowIfCancellationRequested();
                    if (data[position] != 0xff) { position++; continue; }
                    int markerStart = position++;
                    while (position < data.Length && data[position] == 0xff) position++;
                    if (position == data.Length) throw Error("Truncated scan marker.");
                    byte code = data[position];
                    if (code == 0 || code is >= 0xd0 and <= 0xd7) { position++; continue; }
                    position = markerStart;
                    break;
                }
                segments.Add(new(start, position - start, start, 0, -1, MetadataKinds.None));
                scan = false;
                if (position == data.Length) throw Error("Missing EOI marker.");
            }
            int begin = position;
            if (data[position++] != 0xff) throw Error("Invalid JPEG marker.");
            while (position < data.Length && data[position] == 0xff) position++;
            if (position == data.Length) throw Error("Truncated JPEG marker.");
            int marker = data[position++];
            if (marker == 0xd9)
            {
                if (!sawScan) throw Error("Missing image scan.");
                segments.Add(new(begin, data.Length - begin, position, 0, marker, MetadataKinds.None));
                return segments;
            }
            if (marker is 0 or 0xd8 or >= 0xd0 and <= 0xd7) throw Error("Unexpected standalone marker.");
            if (marker == 1) { segments.Add(new(begin, position - begin, position, 0, marker, MetadataKinds.None)); continue; }
            if (data.Length - position < 2) throw Error("Truncated segment length.");
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || length > data.Length - position) throw Error("Truncated JPEG segment.");
            int bodyStart = position + 2;
            ReadOnlySpan<byte> body = data.Slice(bodyStart, length - 2);
            MetadataKinds kind = marker == 0xe1 && body.StartsWith(ExifPrefix) ? MetadataKinds.Exif
                : marker == 0xe1 && body.StartsWith(XmpPrefix) ? MetadataKinds.Xmp
                : marker == 0xe2 && body.StartsWith(IccPrefix) ? MetadataKinds.Icc : MetadataKinds.None;
            if (marker == 0xe2 && body.StartsWith("MPF\0"u8))
                throw new NotSupportedException("JPEG MPF/MPO offsets cannot be preserved by this metadata editor.");
            if (marker == 0xe1 && body.StartsWith("http://ns.adobe.com/xmp/extension/\0"u8))
                throw new NotSupportedException("Extended JPEG XMP is not supported for metadata editing.");
            position += length;
            segments.Add(new(begin, position - begin, bodyStart, length - 2, marker, kind));
            if (marker == 0xda) { scan = true; sawScan = true; }
        }
        throw Error("Missing EOI marker.");
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.Jpeg, message);
}
