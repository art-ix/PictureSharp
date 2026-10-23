using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Parses gain map metadata independently of the container: the ISO 21496-1 binary payload and the Apple model
/// (MakerNote headroom and the map's XMP). Shared by the JPEG reader and format plugins; errors carry the format given.
/// </summary>
internal static class GainMapMetadataParser
{
    // ------------------------------------------------------------------ ISO 21496-1

    /// <summary>Checks the version of the ISO 21496-1 segment (the primary image carries only the version).</summary>
    public static void ReadIsoVersion(ReadOnlySpan<byte> s, ImageFormat? format)
    {
        if (s.Length < 4)
        {
            throw new ImageFormatException(format, "The ISO 21496-1 segment is truncated.");
        }

        int minimumVersion = BinaryPrimitives.ReadUInt16BigEndian(s);
        if (minimumVersion != 0)
        {
            throw new NotSupportedException($"ISO 21496-1 gain map metadata version {minimumVersion} is not supported.");
        }
    }

    // ISO 21496-1 binary metadata (big-endian), as libultrahdr v2.0.2 gainmapmetadata.cpp reads it: u16 minimum and
    // writer versions, a flags byte (0x80 multi-channel, 0x40 use base colour space, 0x08 common denominator,
    // 0x04 backward direction), log2 headrooms of the base and alternate renditions, then per channel the log2 gain
    // map min/max, gamma and the base/alternate offsets as rationals.
    // alternateIcc is the gain map's ICC profile (empty when it has none), needed unless the base colour space is used.
    public static GainMapParameters ReadIso(ReadOnlySpan<byte> s, int mapComponents, ReadOnlySpan<byte> alternateIcc, ImageFormat? format)
    {
        ReadIsoVersion(s, format);
        if (s.Length < 5)
        {
            throw Error(format, "The gain map image has no ISO 21496-1 metadata, only a version.");
        }

        byte flags = s[4];
        int channels = (flags & 0x80) != 0 ? 3 : 1;
        bool useBaseColorSpace = (flags & 0x40) != 0;
        bool commonDenominator = (flags & 0x08) != 0;
        if ((flags & 0x04) != 0)
        {
            throw new NotSupportedException("ISO 21496-1 gain maps with an HDR base image are not supported.");
        }

        int needed = commonDenominator ? 5 + 12 + (channels * 20) : 5 + 16 + (channels * 40);
        if (s.Length < needed)
        {
            throw Error(format, "The ISO 21496-1 metadata is truncated.");
        }

        if (channels == 3 && mapComponents != 3)
        {
            throw Error(format, "The ISO 21496-1 metadata describes three channels but the gain map is gray.");
        }

        var reader = new RationalReader(s[5..], commonDenominator, format);
        float baseHeadroom = reader.Unsigned();
        float alternateHeadroom = reader.Unsigned();
        float[] min = new float[3], max = new float[3], gamma = new float[3], baseOffset = new float[3], alternateOffset = new float[3];
        for (int c = 0; c < channels; c++)
        {
            min[c] = reader.Signed();
            max[c] = reader.Signed();
            gamma[c] = reader.Unsigned();
            baseOffset[c] = reader.Signed();
            alternateOffset[c] = reader.Signed();
            // The checks of libultrahdr v2.0.2 uhdr_validate_gainmap_metadata_descriptor.
            if (!(gamma[c] > 0) || min[c] > max[c] || baseOffset[c] < 0 || alternateOffset[c] < 0 || Math.Abs(min[c]) > 64 || Math.Abs(max[c]) > 64)
            {
                throw Error(format, "The ISO 21496-1 metadata has an invalid gamma, gain range or offset.");
            }
        }

        for (int c = channels; c < 3; c++)
        {
            (min[c], max[c], gamma[c], baseOffset[c], alternateOffset[c]) = (min[0], max[0], gamma[0], baseOffset[0], alternateOffset[0]);
        }

        if (baseHeadroom < 0 || alternateHeadroom > 64)
        {
            throw Error(format, "The ISO 21496-1 metadata has an invalid headroom.");
        }

        if (!(alternateHeadroom > baseHeadroom))
        {
            throw new NotSupportedException("ISO 21496-1 gain maps whose base rendition is not the SDR one are not supported.");
        }

        HdrColorSpace? multiplicationSpace = null;
        if (!useBaseColorSpace)
        {
            // The alternate rendition's primaries come from the map's ICC profile; its transfer curve (Apple writes a
            // PQ-like "Adaptive Gain Curve") does not apply, map samples are plain numbers.
            if (alternateIcc.IsEmpty
                || IccProfileParser.Parse(alternateIcc, format).Colorants is not { } colorants
                || HdrColorSpaces.Classify(colorants) is not { } space)
            {
                throw new NotSupportedException("The ISO 21496-1 alternate color space must be sRGB or Display P3, declared by the gain map's ICC profile.");
            }

            multiplicationSpace = space;
        }

        return new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            ChannelCount = channels,
            GainMapMin = min,
            GainMapMax = max,
            Gamma = gamma,
            BaseOffset = baseOffset,
            AlternateOffset = alternateOffset,
            BaseHeadroom = baseHeadroom,
            AlternateHeadroom = alternateHeadroom,
            MultiplicationSpace = multiplicationSpace,
            UseBaseColorSpace = useBaseColorSpace,
        };
    }

    /// <summary>Reads ISO 21496-1 fractions, with or without a common denominator.</summary>
    private ref struct RationalReader(ReadOnlySpan<byte> data, bool common, ImageFormat? format)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly uint _denominator = common ? BinaryPrimitives.ReadUInt32BigEndian(data) : 0;
        private int _pos = common ? 4 : 0;

        public float Unsigned() => (float)Next(signed: false);

        public float Signed() => (float)Next(signed: true);

        private double Next(bool signed)
        {
            uint raw = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
            _pos += 4;
            uint denominator = _denominator;
            if (!common)
            {
                denominator = BinaryPrimitives.ReadUInt32BigEndian(_data[_pos..]);
                _pos += 4;
            }

            if (denominator == 0)
            {
                throw Error(format, "The ISO 21496-1 metadata has a zero denominator.");
            }

            return (signed ? (double)(int)raw : raw) / denominator;
        }
    }

    // ------------------------------------------------------------------ Apple HDR Gain Map

    /// <summary>
    /// The Apple model: the headroom from MakerNote tags 33 and 48 (or, without them, the map's
    /// <c>HDRGainMapHeadroom</c> XMP), versions 1.x and 2.x of <c>HDRGainMapVersion</c>.
    /// </summary>
    // exif is the TIFF structure of the base image's EXIF block (empty when it has none).
    public static GainMapParameters ReadApple(ReadOnlySpan<byte> exif, ReadOnlySpan<byte> mapXmp, ImageFormat? format)
    {
        if (ReadXmpNumber(mapXmp, "HDRGainMapVersion"u8) is not { } version || version < 0x10000 || version >= 0x30000)
        {
            throw new NotSupportedException("This Apple HDRGainMapVersion is not supported.");
        }

        double? headroom = ReadAppleMakerNote(exif) is ({ } maker33, { } maker48)
            ? AppleHeadroom(maker33, maker48)
            : ReadXmpNumber(mapXmp, "HDRGainMapHeadroom"u8);
        if (headroom is not { } h || !double.IsFinite(h) || h < 1 || h > 1e6)
        {
            throw Error(format, "The Apple HDR headroom (MakerNote tags 33 and 48) is missing or invalid.");
        }

        return new GainMapParameters
        {
            Kind = HdrGainMapKinds.Apple,
            AlternateHeadroom = (float)Math.Log2(h),
        };
    }

    // The headroom formula of "Applying Apple HDR effect to your photos" (Apple developer documentation).
    public static double AppleHeadroom(double maker33, double maker48)
    {
        double stops = maker33 < 1.0
            ? (maker48 <= 0.01 ? (-20.0 * maker48) + 1.8 : (-0.101 * maker48) + 1.601)
            : (maker48 <= 0.01 ? (-70.0 * maker48) + 3.0 : (-0.303 * maker48) + 2.303);
        return Math.Pow(2.0, Math.Max(stops, 0.0));
    }

    /// <summary>Tags 33 and 48 of the Apple MakerNote inside the EXIF block, when present.</summary>
    private static (double? Tag33, double? Tag48) ReadAppleMakerNote(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return default;
        }

        bool big = tiff[0] == 'M';
        int exifIfd = FindEntry(tiff, Read32(tiff[4..], big), 0x8769, big);
        int makerEntry = exifIfd < 0 ? -1 : FindEntry(tiff, Read32(tiff[(exifIfd + 8)..], big), 0x927C, big);
        if (makerEntry < 0)
        {
            return default;
        }

        uint length = Read32(tiff[(makerEntry + 4)..], big);
        uint offset = Read32(tiff[(makerEntry + 8)..], big);
        if (length < 16 || offset > (uint)tiff.Length || length > (uint)tiff.Length - offset)
        {
            return default;
        }

        // "Apple iOS\0", u16 version, "MM", then an IFD whose offsets are relative to the MakerNote start.
        ReadOnlySpan<byte> note = tiff.Slice((int)offset, (int)length);
        if (!note.StartsWith("Apple iOS\0"u8) || note[12] != 'M' || note[13] != 'M')
        {
            return default;
        }

        return (ReadRational(note, 14, 33), ReadRational(note, 14, 48));
    }

    private static double? ReadRational(ReadOnlySpan<byte> tiff, uint ifd, ushort tag)
    {
        int entry = FindEntry(tiff, ifd, tag, big: true);
        if (entry < 0)
        {
            return null;
        }

        ushort type = Read16(tiff[(entry + 2)..], true);
        uint offset = Read32(tiff[(entry + 8)..], true);
        if (type is not (5 or 10) || tiff.Length < 8 || offset > (uint)tiff.Length - 8)
        {
            return null;
        }

        uint numerator = Read32(tiff[(int)offset..], true), denominator = Read32(tiff[((int)offset + 4)..], true);
        if (denominator == 0)
        {
            return null;
        }

        return type == 10 ? (double)(int)numerator / (int)denominator : (double)numerator / denominator;
    }

    /// <summary>The offset of the 12-byte entry of <paramref name="tag"/> in the directory at <paramref name="ifd"/>, or -1.</summary>
    private static int FindEntry(ReadOnlySpan<byte> tiff, uint ifd, ushort tag, bool big)
    {
        if (tiff.Length < 2 || ifd > (uint)tiff.Length - 2)
        {
            return -1;
        }

        int count = Read16(tiff[(int)ifd..], big);
        for (int i = 0; i < count; i++)
        {
            long at = ifd + 2 + (12L * i);
            if (at + 12 > tiff.Length)
            {
                return -1;
            }

            if (Read16(tiff[(int)at..], big) == tag)
            {
                return (int)at;
            }
        }

        return -1;
    }

    /// <summary>
    /// A number in an XMP packet, written as an attribute (<c>ns:Name="1.5"</c>) or an element (<c>&lt;ns:Name&gt;1.5&lt;</c>).
    /// </summary>
    private static double? ReadXmpNumber(ReadOnlySpan<byte> xmp, ReadOnlySpan<byte> localName)
    {
        int at = xmp.IndexOf(localName);
        while (at >= 0)
        {
            ReadOnlySpan<byte> rest = xmp[(at + localName.Length)..];
            if (at > 0 && xmp[at - 1] == ':' && rest.Length > 1 && (rest[0] == '>' || (rest[0] == '=' && rest[1] is (byte)'"' or (byte)'\'')))
            {
                ReadOnlySpan<byte> value = rest[(rest[0] == '>' ? 1 : 2)..];
                int end = value.IndexOfAny("\"'<"u8);
                if (end > 0 && end <= 64
                    && double.TryParse(Encoding.ASCII.GetString(value[..end]).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                {
                    return number;
                }

                return null;
            }

            int next = rest.IndexOf(localName);
            at = next < 0 ? -1 : at + localName.Length + next;
        }

        return null;
    }
    private static ImageFormatException Error(ImageFormat? format, string message) => new(format, message);

    private static ushort Read16(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint Read32(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
