using System.Buffers.Binary;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Hdr;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats.Jpeg;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>
/// Writes a JPEG with the attached ISO 21496-1 gain map as a second MPF image, laid out as libultrahdr v2.0.2
/// writes it with its default build (ISO metadata, no XMP). The ISO payload and the map's ICC profile are copied from
/// the source; an unedited map is copied without re-encoding.
/// </summary>
internal static class JpegGainMapWriter
{
    private static ReadOnlySpan<byte> IsoPrefix => "urn:iso:std:iso:ts:21496:-1\0"u8;
    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;
    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> XmpPrefix => "http://ns.adobe.com/xap/1.0/\0"u8;

    /// <summary>The segments of one JPEG image: selected APPn segments (whole, with marker) and the rest without APPn.</summary>
    private sealed class Parts
    {
        public byte[]? IsoPayload;
        public int Components;
        public byte[]? App0, Exif, Xmp;
        public readonly List<byte[]> Icc = [];
        public readonly MemoryStream Tables = new();   // non-APPn segments before the first scan
        public int ScanStart;                          // offset of the first SOS
    }

    public static void Encode(Image image, Stream output, JpegEncoderOptions options, CancellationToken cancellationToken)
    {
        if (Resolve(image, options.GainMap, cancellationToken) is not ({ } attachment, { } source))
        {
            JpegEncoder.EncodeImage(image, output, options, cancellationToken);
            return;
        }

        ReadOnlySpan<byte> mapFile = attachment.MapData.Span;
        byte[] mapJpeg = mapFile.ToArray();
        Parts map = source;
        if (attachment.EditedMap is { } edited)
        {
            var pixels = (Rgba32[])edited.Pixels.Clone();
            using var mapImage = new Image([new ImageFrame(edited.Width, edited.Height, pixels)]);
            var mapStream = new MemoryStream();
            var mapOptions = new JpegEncoderOptions
            {
                Quality = options.GainMapQuality,
                Subsampling = JpegSubsampling.Ratio444,
                Metadata = MetadataKinds.None,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
            };
            JpegEncoder.EncodeImage(mapImage, mapStream, mapOptions, cancellationToken, color: source.Components == 3);
            mapJpeg = mapStream.ToArray();
            map = Split(mapJpeg) ?? throw new InvalidOperationException("The encoded gain map has no scan.");
        }

        // A user XMP packet that declares a gain map (Ultra HDR XMP or a GContainer directory) describes the source's
        // items, not this file's, so it is left out.
        MetadataKinds kinds = options.Metadata;
        if (image.Metadata.XmpData is { } xmp && DeclaresGainMap(xmp))
        {
            kinds &= ~MetadataKinds.Xmp;
        }

        var baseStream = new MemoryStream();
        JpegEncoder.EncodeImage(image, baseStream, options with { GainMap = JpegGainMapMode.None, Metadata = kinds }, cancellationToken, color: true);
        byte[] baseJpeg = baseStream.ToArray();
        Parts primary = Split(baseJpeg) ?? throw new InvalidOperationException("The encoded base image has no scan.");
        cancellationToken.ThrowIfCancellationRequested();

        // Secondary image: SOI, ISO metadata, the map's ICC profile, then the map without its APPn segments.
        var secondary = new MemoryStream();
        secondary.Write([0xFF, 0xD8]);
        WriteSegment(secondary, 0xE2, IsoPrefix, source.IsoPayload!);
        foreach (byte[] icc in source.Icc)
        {
            secondary.Write(icc);
        }

        map.Tables.WriteTo(secondary);
        secondary.Write(mapJpeg.AsSpan(map.ScanStart));

        // port: libultrahdr lib/src/jpegr.cpp JpegR::appendGainMap (UHDR_WRITE_ISO, no XMP)
        var head = new MemoryStream();
        head.Write([0xFF, 0xD8]);
        if (primary.App0 is { } app0) head.Write(app0);
        if (primary.Exif is { } exif) head.Write(exif);
        if (primary.Xmp is { } baseXmp) head.Write(baseXmp);
        foreach (byte[] icc in primary.Icc) head.Write(icc);
        WriteSegment(head, 0xE2, IsoPrefix, [0, 0, 0, 0]);   // minimum_version, writer_version
        primary.Tables.WriteTo(head);

        const int MpfSegment = 2 + 2 + 4 + 8 + 2 + (3 * 12) + 4 + 32;   // marker, length, "MPF\0", TIFF data
        long primarySize = head.Length + MpfSegment + (baseJpeg.Length - primary.ScanStart);
        long tiffStart = head.Length + 8;
        long secondaryOffset = primarySize - tiffStart;
        if (primarySize > uint.MaxValue || secondary.Length > uint.MaxValue)
        {
            throw new NotSupportedException("A JPEG with a gain map must be smaller than 4 GiB.");
        }

        head.WriteTo(output);
        WriteMpf(output, (uint)primarySize, (uint)secondary.Length, (uint)secondaryOffset);
        output.Write(baseJpeg.AsSpan(primary.ScanStart));
        secondary.WriteTo(output);
    }

    /// <summary>
    /// The attachment to write and the segments of its source map, or null when the file is written without a map.
    /// Throws for <see cref="JpegGainMapMode.Require"/> when no map can be written.
    /// </summary>
    private static (GainMapAttachment? Attachment, Parts? Source) Resolve(Image image, JpegGainMapMode mode, CancellationToken cancellationToken)
    {
        bool require = mode == JpegGainMapMode.Require;
        if (image.GainMap is not { } attachment)
        {
            return require
                ? throw new InvalidOperationException("The image has no HDR gain map to write: load it with DecoderOptions.LoadGainMap = true and keep it through geometric operations only.")
                : default;
        }

        if (!attachment.CanDecode)
        {
            string reason = attachment.Info.UnsupportedReason ?? "The HDR gain map cannot be written.";
            return !require ? default
                : attachment.Failure == GainMapFailure.Unsupported ? throw new NotSupportedException(reason)
                : throw new ImageFormatException(ImageFormat.Jpeg, reason);
        }

        if (attachment.Parameters!.Kind != HdrGainMapKinds.Iso21496)
        {
            return require
                ? throw new NotSupportedException("Only ISO 21496-1 gain maps can be written; this map is described by Apple metadata only.")
                : default;
        }

        if (Split(attachment.MapData.Span) is not { IsoPayload: not null } source)
        {
            return require ? throw new ImageFormatException(ImageFormat.Jpeg, "The HDR gain map image is corrupt.") : default;
        }

        // A map oriented while loading is written in the orientation of the base pixels.
        (int turns, bool flipX) = Geometry.FromExifOrientation(attachment.AppliedOrientation);
        if (attachment.EditedMap is null && (turns != 0 || flipX))
        {
            attachment = GainMapEditing.Apply(attachment, image.Size, GainMapEdit.RotateFlip(0, false), cancellationToken);
            if (!attachment.CanDecode)
            {
                return require ? throw new ImageFormatException(ImageFormat.Jpeg, "The HDR gain map image is corrupt.") : default;
            }
        }

        return (attachment, source);
    }

    private static bool DeclaresGainMap(ReadOnlySpan<byte> xmp) =>
        xmp.IndexOf("http://ns.adobe.com/hdr-gain-map/1.0/"u8) >= 0 || xmp.IndexOf("http://ns.google.com/photos/1.0/container/"u8) >= 0;

    /// <summary>The segments of a JPEG up to its first scan, or null when it is not a well-formed JPEG header.</summary>
    private static Parts? Split(ReadOnlySpan<byte> jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            return null;
        }

        var parts = new Parts();
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF)
            {
                return null;
            }

            int marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos++;   // fill byte
                continue;
            }

            if (marker == 0xDA)
            {
                parts.ScanStart = pos;
                return parts.Components is 1 or 3 ? parts : null;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            if (length < 2 || pos + 2 + length > jpeg.Length)
            {
                return null;
            }

            ReadOnlySpan<byte> segment = jpeg.Slice(pos, 2 + length);
            ReadOnlySpan<byte> body = segment[4..];
            switch (marker)
            {
                case 0xE0 when parts.App0 is null:
                    parts.App0 = segment.ToArray();
                    break;
                case 0xE1 when parts.Exif is null && body.StartsWith(ExifPrefix):
                    parts.Exif = segment.ToArray();
                    break;
                case 0xE1 when parts.Xmp is null && body.StartsWith(XmpPrefix):
                    parts.Xmp = segment.ToArray();
                    break;
                case 0xE2 when body.StartsWith(IccPrefix):
                    parts.Icc.Add(segment.ToArray());
                    break;
                case 0xE2 when parts.IsoPayload is null && body.StartsWith(IsoPrefix):
                    parts.IsoPayload = body[IsoPrefix.Length..].ToArray();
                    break;
                case >= 0xE0 and <= 0xEF:
                    break;   // other APPn segments (MPF, Apple XMP, ...) are not carried over
                case 0xC0 or 0xC1 or 0xC2 when length >= 8:
                    parts.Components = body[5];
                    parts.Tables.Write(segment);
                    break;
                default:
                    parts.Tables.Write(segment);
                    break;
            }

            pos += 2 + length;
        }

        return null;
    }

    private static void WriteSegment(Stream output, int marker, ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> payload)
    {
        int length = 2 + prefix.Length + payload.Length;
        if (length > 65535)
        {
            throw new NotSupportedException("The HDR gain map metadata does not fit in one JPEG segment.");
        }

        output.Write([0xFF, (byte)marker, (byte)(length >> 8), (byte)length]);
        output.Write(prefix);
        output.Write(payload);
    }

    // port: libultrahdr lib/src/multipictureformat.cpp generateMpf (big-endian)
    private static void WriteMpf(Stream output, uint primarySize, uint secondarySize, uint secondaryOffset)
    {
        Span<byte> tiff = stackalloc byte[8 + 2 + (3 * 12) + 4 + 32];
        "MM\0*"u8.CopyTo(tiff);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[4..], 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff[8..], 3);
        Span<byte> tags = tiff[10..];
        Tag(tags, 0xB000, 7, 4, 0);
        "0100"u8.CopyTo(tags[8..]);
        Tag(tags[12..], 0xB001, 4, 1, 2);
        Tag(tags[24..], 0xB002, 7, 32, 8 + 2 + (3 * 12) + 4);
        BinaryPrimitives.WriteUInt32BigEndian(tiff[46..], 0);   // no attribute IFD
        Span<byte> entries = tiff[50..];
        BinaryPrimitives.WriteUInt32BigEndian(entries, 0x030000);   // JPEG, primary image
        BinaryPrimitives.WriteUInt32BigEndian(entries[4..], primarySize);
        BinaryPrimitives.WriteUInt32BigEndian(entries[8..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[16..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[20..], secondarySize);
        BinaryPrimitives.WriteUInt32BigEndian(entries[24..], secondaryOffset);
        BinaryPrimitives.WriteUInt32BigEndian(entries[12..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(entries[28..], 0);
        WriteSegment(output, 0xE2, "MPF\0"u8, tiff);

        static void Tag(Span<byte> s, ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s, tag);
            BinaryPrimitives.WriteUInt16BigEndian(s[2..], type);
            BinaryPrimitives.WriteUInt32BigEndian(s[4..], count);
            BinaryPrimitives.WriteUInt32BigEndian(s[8..], value);
        }
    }
}
