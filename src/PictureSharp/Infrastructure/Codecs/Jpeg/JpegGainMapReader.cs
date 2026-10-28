using System.Buffers.Binary;
using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>
/// Finds an HDR gain map in a JPEG file: the primary image's MPF directory points to secondary JPEG images, and the
/// one that declares itself a gain map (ISO 21496-1 metadata in APP2, or Apple's <c>HDRGainMapVersion</c> XMP) is
/// selected. The MPF entry type is not trusted (Apple writes 0), and neither the EXIF thumbnail nor any other
/// secondary image becomes a gain map. Never throws for HDR data: problems become a failed attachment, so the base
/// image loads as before.
/// </summary>
internal static class JpegGainMapReader
{
    private static ReadOnlySpan<byte> ExifPrefix => "Exif\0\0"u8;
    private static ReadOnlySpan<byte> XmpPrefix => "http://ns.adobe.com/xap/1.0/\0"u8;
    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;
    private static ReadOnlySpan<byte> MpfPrefix => "MPF\0"u8;
    private static ReadOnlySpan<byte> IsoPrefix => "urn:iso:std:iso:ts:21496:-1\0"u8;

    /// <summary>The markers and selected segments of one JPEG image up to its first scan.</summary>
    private sealed class JpegHeader
    {
        public int Width;
        public int Height;
        public int Components;
        public Range? Exif;
        public Range? Xmp;
        public Range? Iso;
        public Range? Mpf;
        public int MpfTiffStart;
        public readonly List<Range> IccChunks = [];
        public long MetadataBytes;
    }

    /// <summary>
    /// Reads the gain map declared by <paramref name="file"/>, or returns null when it declares none.
    /// </summary>
    /// <param name="file">The whole JPEG file.</param>
    /// <param name="options">Decoder options (limits).</param>
    /// <param name="budget">The load's budget, reserving the map pixels and HDR metadata; null for identification.</param>
    /// <param name="copyMap">Copy the compressed map into the attachment (loading) rather than only describing it.</param>
    public static GainMapAttachment? Read(ReadOnlySpan<byte> file, DecoderOptions options, DecodeBudget? budget, bool copyMap)
    {
        JpegHeader primary;
        try
        {
            if (ReadHeader(file, 0, file.Length) is not { } header)
            {
                return null;   // the base decoder reports structural errors of the primary image
            }

            primary = header;
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            return null;
        }

        var info = new GainMapInfo();
        try
        {
            return ReadCore(file, primary, options, budget, copyMap, ref info);
        }
        catch (ImageFormatException ex)
        {
            return GainMapAttachment.Failed(info, GainMapFailure.Corrupt, ex.Message);
        }
        catch (NotSupportedException ex)
        {
            return GainMapAttachment.Failed(info, GainMapFailure.Unsupported, ex.Message);
        }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
        {
            return GainMapAttachment.Failed(info, GainMapFailure.Corrupt, "The HDR gain map data is corrupt.");
        }
    }

    private static GainMapAttachment? ReadCore(ReadOnlySpan<byte> file, JpegHeader primary, DecoderOptions options, DecodeBudget? budget,
        bool copyMap, ref GainMapInfo info)
    {
        bool primaryUltraHdr = primary.Xmp is { } px && file[px].IndexOf("hdrgm:"u8) >= 0;
        if (primary.Mpf is null)
        {
            if (primary.Iso is null && !primaryUltraHdr)
            {
                return null;
            }

            info = info with { Detected = (primary.Iso is null ? 0 : HdrGainMapKinds.Iso21496) | (primaryUltraHdr ? HdrGainMapKinds.UltraHdrXmp : 0) };
            throw Error("The file declares an HDR gain map but has no MPF directory pointing to it.");
        }

        // The secondary image that declares a gain map; other MPF images (depth maps, mattes) are skipped.
        (int mapStart, int mapLength, JpegHeader map)? found = null;
        foreach ((int start, int length) in ReadMpfEntries(file, primary))
        {
            if (ReadHeader(file, start, length) is not { } header)
            {
                continue;
            }

            if (header.Iso is not null || (header.Xmp is { } x && IsGainMapXmp(file[x])))
            {
                found = (start, length, header);
                break;
            }
        }

        if (found is not { } gainMap)
        {
            if (primary.Iso is null && !primaryUltraHdr)
            {
                return null;   // a plain multi-picture file (MPO, depth map)
            }

            info = info with { Detected = (primary.Iso is null ? 0 : HdrGainMapKinds.Iso21496) | (primaryUltraHdr ? HdrGainMapKinds.UltraHdrXmp : 0) };
            throw Error("The HDR gain map image declared by the file was not found.");
        }

        JpegHeader m = gainMap.map;
        ReadOnlySpan<byte> mapXmp = m.Xmp is { } mx ? file[mx] : default;
        HdrGainMapKinds detected =
            (m.Iso is not null || primary.Iso is not null ? HdrGainMapKinds.Iso21496 : 0)
            | (mapXmp.IndexOf("HDRGainMapVersion"u8) >= 0 ? HdrGainMapKinds.Apple : 0)
            | (primaryUltraHdr || mapXmp.IndexOf("hdrgm:"u8) >= 0 ? HdrGainMapKinds.UltraHdrXmp : 0);
        info = info with { Detected = detected, GainMapSize = new Size(m.Width, m.Height) };

        if (m.Width <= 0 || m.Height <= 0 || m.Components is not (1 or 3))
        {
            throw Error("The gain map image has an invalid frame header.");
        }

        if (budget is not null)
        {
            budget.ReserveMetadata(ImageFormat.Jpeg, m.MetadataBytes);
            if (!budget.TryReservePixels(m.Width, m.Height))
            {
                throw Error("The gain map exceeds DecoderOptions.MaxPixels.");
            }
        }

        GainMapParameters parameters;
        if ((detected & HdrGainMapKinds.Iso21496) != 0)
        {
            // ISO wins; corrupt or unsupported ISO metadata never falls back to the Apple model.
            if (primary.Iso is { } primaryIso)
            {
                GainMapMetadataParser.ReadIsoVersion(file[primaryIso], ImageFormat.Jpeg);
            }

            if (m.Iso is not { } iso)
            {
                throw Error("The gain map image has no ISO 21496-1 metadata.");
            }

            parameters = GainMapMetadataParser.ReadIso(file[iso], m.Components, JoinIcc(file, m.IccChunks), ImageFormat.Jpeg);
        }
        else if ((detected & HdrGainMapKinds.Apple) != 0)
        {
            parameters = GainMapMetadataParser.ReadApple(primary.Exif is { } exif ? file[exif] : default, mapXmp, ImageFormat.Jpeg);
        }
        else
        {
            throw new NotSupportedException("Ultra HDR gain maps described only by XMP (without ISO 21496-1 metadata) are not supported.");
        }

        if (primary.Components != 3)
        {
            throw new NotSupportedException("HDR reconstruction requires an RGB (YCbCr) base image.");
        }

        (HdrColorSpace baseSpace, BaseTransfer? transfer) = ReadBaseColor(file, primary);
        info = info with
        {
            Selected = parameters.Kind,
            Headroom = float.Exp2(Math.Max(parameters.AlternateHeadroom, parameters.BaseHeadroom)),
            CanDecode = true,
        };

        return new GainMapAttachment
        {
            Info = info,
            Parameters = parameters with { MultiplicationSpace = parameters.MultiplicationSpace ?? baseSpace },
            MapData = copyMap ? file.Slice(gainMap.mapStart, gainMap.mapLength).ToArray() : default,
            Decoder = JpegGainMapDecoder.Instance,
            BaseSpace = baseSpace,
            BaseTransfer = transfer,
        };
    }

    private static bool IsGainMapXmp(ReadOnlySpan<byte> xmp) =>
        xmp.IndexOf("HDRGainMapVersion"u8) >= 0 || xmp.IndexOf(":hdrgainmap"u8) >= 0 || xmp.IndexOf("hdrgm:Version"u8) >= 0;

    private static ImageFormatException Error(string message) => new(ImageFormat.Jpeg, message);

    /// <summary>The primaries and transfer function of the base image from its ICC profile; no profile means sRGB.</summary>
    private static (HdrColorSpace Space, BaseTransfer? Transfer) ReadBaseColor(ReadOnlySpan<byte> file, JpegHeader primary) =>
        HdrBaseColor.FromModel(JoinIcc(file, primary.IccChunks) is { } icc ? IccProfileParser.Parse(icc, ImageFormat.Jpeg) : null);

    /// <summary>Joins the APP2 chunks of an ICC profile; an incomplete or inconsistent set counts as none.</summary>
    private static byte[]? JoinIcc(ReadOnlySpan<byte> file, List<Range> chunks)
    {
        if (chunks.Count == 0)
        {
            return null;
        }

        int count = file[chunks[0]][IccPrefix.Length + 1];
        if (count != chunks.Count)
        {
            return null;
        }

        var ordered = new Range?[count];
        int total = 0;
        foreach (Range chunk in chunks)
        {
            ReadOnlySpan<byte> s = file[chunk];
            int seq = s[IccPrefix.Length];
            if (s[IccPrefix.Length + 1] != count || seq < 1 || seq > count || ordered[seq - 1] is not null)
            {
                return null;
            }

            ordered[seq - 1] = chunk;
            total += s.Length - IccPrefix.Length - 2;
        }

        var icc = new byte[total];
        int pos = 0;
        foreach (Range? chunk in ordered)
        {
            ReadOnlySpan<byte> data = file[chunk!.Value][(IccPrefix.Length + 2)..];
            data.CopyTo(icc.AsSpan(pos));
            pos += data.Length;
        }

        return icc;
    }

    /// <summary>
    /// Reads the marker segments of the JPEG image at <paramref name="start"/> up to its first scan, or returns null
    /// when it is not a well-formed JPEG header.
    /// </summary>
    private static JpegHeader? ReadHeader(ReadOnlySpan<byte> file, int start, int length)
    {
        ReadOnlySpan<byte> jpeg = file.Slice(start, length);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != JpegMarkers.Soi)
        {
            return null;
        }

        var header = new JpegHeader();
        int pos = 2;
        while (true)
        {
            if (pos + 4 > jpeg.Length || jpeg[pos] != 0xFF)
            {
                return null;
            }

            int marker = jpeg[pos + 1];
            if (marker == 0xFF)
            {
                pos++;   // fill byte
                continue;
            }

            if (marker is JpegMarkers.Sos or JpegMarkers.Eoi)
            {
                return header.Width > 0 ? header : null;
            }

            if (marker is (>= JpegMarkers.Rst0 and <= JpegMarkers.Rst7) or JpegMarkers.Tem)
            {
                pos += 2;
                continue;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            if (segmentLength < 2 || segmentLength > jpeg.Length - pos - 2)
            {
                return null;
            }

            int body = pos + 4;
            ReadOnlySpan<byte> s = jpeg.Slice(body, segmentLength - 2);
            var range = new Range(start + body, start + body + s.Length);
            switch (marker)
            {
                case (>= 0xC0 and <= 0xCF) and not JpegMarkers.Dht and not JpegMarkers.Dac when s.Length >= 6:
                    header.Height = BinaryPrimitives.ReadUInt16BigEndian(s[1..]);
                    header.Width = BinaryPrimitives.ReadUInt16BigEndian(s[3..]);
                    header.Components = s[5];
                    break;
                case JpegMarkers.App1 when header.Exif is null && s.StartsWith(ExifPrefix):
                    header.Exif = new Range(range.Start.Value + ExifPrefix.Length, range.End);
                    break;
                case JpegMarkers.App1 when header.Xmp is null && s.StartsWith(XmpPrefix):
                    header.Xmp = new Range(range.Start.Value + XmpPrefix.Length, range.End);
                    header.MetadataBytes += s.Length;
                    break;
                case JpegMarkers.App2 when header.Mpf is null && s.StartsWith(MpfPrefix):
                    header.Mpf = new Range(range.Start.Value + MpfPrefix.Length, range.End);
                    header.MpfTiffStart = range.Start.Value + MpfPrefix.Length;
                    break;
                case JpegMarkers.App2 when header.Iso is null && s.StartsWith(IsoPrefix):
                    header.Iso = new Range(range.Start.Value + IsoPrefix.Length, range.End);
                    header.MetadataBytes += s.Length;
                    break;
                case JpegMarkers.App2 when s.Length > IccPrefix.Length + 2 && s.StartsWith(IccPrefix):
                    header.IccChunks.Add(range);
                    break;
            }

            pos = body + s.Length;
        }
    }

    /// <summary>The (start, length) of every secondary image listed by the primary image's MPF directory.</summary>
    private static List<(int Start, int Length)> ReadMpfEntries(ReadOnlySpan<byte> file, JpegHeader primary)
    {
        ReadOnlySpan<byte> tiff = file[primary.Mpf!.Value];
        if (tiff.Length < 8)
        {
            throw Error("The MPF segment is truncated.");
        }

        bool big = tiff[0] == 'M' && tiff[1] == 'M';
        if (!big && !(tiff[0] == 'I' && tiff[1] == 'I'))
        {
            throw Error("The MPF segment has an invalid byte order.");
        }

        uint ifd = Read32(tiff[4..], big);
        if (ifd > (uint)tiff.Length - 2)
        {
            throw Error("The MPF index directory lies outside the segment.");
        }

        int count = Read16(tiff[(int)ifd..], big);
        if ((long)ifd + 2 + (12L * count) > tiff.Length)
        {
            throw Error("The MPF index directory is truncated.");
        }

        List<(int, int)> entries = [];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = tiff.Slice((int)ifd + 2 + (12 * i), 12);
            if (Read16(entry, big) != 0xB002)
            {
                continue;   // MP Entry
            }

            uint bytes = Read32(entry[4..], big);
            uint offset = Read32(entry[8..], big);
            if (bytes % 16 != 0 || offset > (uint)tiff.Length || bytes > (uint)tiff.Length - offset)
            {
                throw Error("The MPF entry list lies outside the segment.");
            }

            for (int e = 0; e < bytes / 16; e++)
            {
                ReadOnlySpan<byte> mp = tiff.Slice((int)offset + (16 * e), 16);
                uint size = Read32(mp[4..], big);
                uint start = Read32(mp[8..], big);
                if (start == 0)
                {
                    continue;   // the primary image
                }

                // Offsets are relative to the MPF TIFF header.
                long absolute = primary.MpfTiffStart + (long)start;
                if (size < 4 || absolute > file.Length || size > file.Length - absolute)
                {
                    throw Error("An MPF image lies outside the file.");
                }

                entries.Add(((int)absolute, (int)size));
            }

            break;
        }

        return entries;
    }

    private static ushort Read16(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint Read32(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
