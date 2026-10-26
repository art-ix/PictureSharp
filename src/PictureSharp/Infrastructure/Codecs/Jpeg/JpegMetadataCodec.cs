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
    private static ReadOnlySpan<byte> MpfPrefix => "MPF\0"u8;

    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        List<Segment> segments = Parse(data.Span, context.CancellationToken);
        GainMapContainer(data.Span, segments, context);
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

    public byte[] Rewrite(ReadOnlyMemory<byte> data, ImageMetadata original, ImageMetadata metadata, DecodeContext context)
    {
        var segments = Parse(data.Span, context.CancellationToken);
        int mpfIndex = MpfIndex(data.Span, segments);
        GainMapInfo? hdr = GainMapContainer(data.Span, segments, context);
        if (hdr is not null)
        {
            if (!(metadata.IccData ?? []).AsSpan().SequenceEqual(original.IccData ?? []))
                throw new InvalidOperationException("The ICC profile defines the colors of the HDR gain map's base image and cannot be changed or removed; "
                    + "load the image and save it (base SDR) or use ToHdr and ToneMap to get an SDR copy without HDR.");
        }

        if (!metadata.SameColorDescription(original))
            throw new NotSupportedException("JPEG stores colors only as an ICC profile; gamma, chromaticities and sRGB intent are PNG chunks.");
        bool resolution = metadata.HorizontalResolution != original.HorizontalResolution || metadata.VerticalResolution != original.VerticalResolution;
        using var output = new PooledBufferStream();
        output.Write([0xff, 0xd8]);
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
        long newMpf = -1, newPrimaryEnd = -1, oldPrimaryEnd = -1;
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
            if (segment.Marker == 0xe2 && segment.Start == (mpfIndex >= 0 ? segments[mpfIndex].Start : -1)) newMpf = output.Position;
            if (segment.Marker == 0xd9) (newPrimaryEnd, oldPrimaryEnd) = (output.Position + 2, segment.Start + 2);
            MetadataContainer.Write(output, data.Span.Slice(segment.Start, segment.Length), context.CancellationToken);
        }
        byte[] result = MetadataContainer.ToArray(output);
        if (mpfIndex >= 0)
        {
            int oldTiff = segments[mpfIndex].Body + MpfPrefix.Length;
            JpegMpf.Relocate(result, (int)newMpf + 4 + MpfPrefix.Length, oldTiff, (int)oldPrimaryEnd, (int)newPrimaryEnd);
            GainMapInfo? rewritten = JpegGainMapReader.Read(result, context.Options, budget: null, copyMap: false)?.Info;
            if (rewritten != hdr)
                throw new InvalidOperationException("The edit would remove or change metadata the HDR gain map needs (such as the Apple MakerNote headroom).");
        }
        return result;
    }

    private static int MpfIndex(ReadOnlySpan<byte> data, List<Segment> segments)
    {
        for (int i = 0; i < segments.Count; i++)
            if (segments[i].Marker == 0xe2 && data.Slice(segments[i].Body, segments[i].BodyLength).StartsWith(MpfPrefix)) return i;
        return -1;
    }

    /// <summary>
    /// The gain map of a file with an MPF directory, or null without MPF. Only HDR gain map containers are supported:
    /// their secondary image is the map, whose MPF offset a rewrite can fix; other MPO files are rejected.
    /// </summary>
    private static GainMapInfo? GainMapContainer(ReadOnlySpan<byte> data, List<Segment> segments, DecodeContext context)
    {
        if (MpfIndex(data, segments) < 0) return null;
        GainMapInfo? hdr = JpegGainMapReader.Read(data, context.Options, budget: null, copyMap: false)?.Info;
        return hdr is { CanDecode: true } ? hdr
            : throw new NotSupportedException("JPEG MPF/MPO offsets can be preserved only for files with a valid HDR gain map.");
    }

    private static void WriteProfiles(Stream output, ImageMetadata metadata, CancellationToken token)
    {
        JpegMarkerWriter.ValidateMetadata(metadata, MetadataKinds.All); // Same limits and exceptions as Save.
        if (metadata.ExifData is { Length: > 0 } exif) WriteSegment(output, 0xe1, [.. ExifPrefix, .. exif], token);
        if (metadata.XmpData is { Length: > 0 } xmp) WriteSegment(output, 0xe1, [.. XmpPrefix, .. xmp], token);
        if (metadata.IccData is not { Length: > 0 } icc) return;
        const int maximum = 65519;
        int count = (int)(((long)icc.Length + maximum - 1) / maximum);
        for (int i = 0; i < count; i++)
        {
            int length = Math.Min(maximum, icc.Length - (i * maximum));
            WriteSegment(output, 0xe2, [.. IccPrefix, (byte)(i + 1), (byte)count, .. icc.AsSpan(i * maximum, length)], token);
        }
    }

    private static void WriteSegment(Stream output, byte marker, ReadOnlySpan<byte> body, CancellationToken token)
    {
        if (body.Length > 65533) throw new ArgumentException("A JPEG marker segment must fit in 65533 bytes.", nameof(body));
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
