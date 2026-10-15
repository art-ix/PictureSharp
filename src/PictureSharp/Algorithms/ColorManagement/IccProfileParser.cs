using System.Buffers.Binary;

namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>
/// Reads the matrix/TRC part of an ICC v2 or v4 profile: RGB colorants with three tone curves, or a gray tone curve,
/// on an XYZ connection space. LUT-based, CMYK, Lab and device-link profiles are not supported.
/// </summary>
// Spec: ICC.1:2022 §7 (header, tag table) and §10 (XYZType, curveType, parametricCurveType)
internal static class IccProfileParser
{
    private const int HeaderSize = 128;

    /// <summary>Parses <paramref name="icc"/>.</summary>
    /// <exception cref="ImageFormatException">The profile is malformed.</exception>
    /// <exception cref="NotSupportedException">The profile is valid but not an RGB or Gray matrix/TRC profile.</exception>
    public static IccColorModel Parse(ReadOnlySpan<byte> icc, ImageFormat? format = null)
    {
        if (icc.Length < HeaderSize + 4)
            throw Corrupt(format, "the profile is shorter than its header");
        uint declared = BinaryPrimitives.ReadUInt32BigEndian(icc);
        if (declared < HeaderSize + 4)
            throw Corrupt(format, "the declared size is smaller than the header");
        // Like LittleCMS, a declared size beyond the data is clamped; the tag table is still checked against the data.
        icc = icc[..(int)Math.Min(declared, (uint)icc.Length)];
        if (!icc.Slice(36, 4).SequenceEqual("acsp"u8))
            throw Corrupt(format, "the 'acsp' signature is missing");

        int major = icc[8];
        if (major is not (2 or 4))
            throw Unsupported($"ICC version {major} is not supported; versions 2 and 4 are.");
        ReadOnlySpan<byte> profileClass = icc.Slice(12, 4);
        if (profileClass.SequenceEqual("link"u8) || profileClass.SequenceEqual("abst"u8) || profileClass.SequenceEqual("nmcl"u8))
            throw Unsupported($"ICC profile class '{Ascii(profileClass)}' cannot convert images.");
        ReadOnlySpan<byte> space = icc.Slice(16, 4);
        bool gray = space.SequenceEqual("GRAY"u8);
        if (!gray && !space.SequenceEqual("RGB "u8))
            throw Unsupported($"ICC color space '{Ascii(space)}' is not supported; RGB and Gray are.");
        if (!icc.Slice(20, 4).SequenceEqual("XYZ "u8))
            throw Unsupported($"ICC connection space '{Ascii(icc.Slice(20, 4))}' is not supported with matrix/TRC; XYZ is.");

        TagTable tags = TagTable.Read(icc, format);
        if (gray)
        {
            if (tags.Find("kTRC"u8) is not { } kTrc)
                throw MissingTags(tags, format, "kTRC");
            return new IccColorModel(ReadCurve(icc, kTrc, format), null, null, null);
        }

        if (tags.Find("rXYZ"u8) is not { } rXyz || tags.Find("gXYZ"u8) is not { } gXyz || tags.Find("bXYZ"u8) is not { } bXyz
            || tags.Find("rTRC"u8) is not { } rTrc || tags.Find("gTRC"u8) is not { } gTrc || tags.Find("bTRC"u8) is not { } bTrc)
            throw MissingTags(tags, format, "rXYZ/gXYZ/bXYZ/rTRC/gTRC/bTRC");

        ColorMatrix colorants = ColorMatrix.FromColumns(ReadXyz(icc, rXyz, format), ReadXyz(icc, gXyz, format), ReadXyz(icc, bXyz, format));
        return new IccColorModel(ReadCurve(icc, rTrc, format), ReadCurve(icc, gTrc, format), ReadCurve(icc, bTrc, format), colorants);
    }

    private static Exception MissingTags(TagTable tags, ImageFormat? format, string names) =>
        tags.Find("A2B0"u8) is not null || tags.Find("A2B1"u8) is not null || tags.Find("A2B2"u8) is not null
            ? Unsupported("LUT-based ICC profiles (A2B tags) are not supported; matrix/TRC profiles are.")
            : Corrupt(format, $"the matrix/TRC tags {names} are missing");

    private static (double X, double Y, double Z) ReadXyz(ReadOnlySpan<byte> icc, Range tag, ImageFormat? format)
    {
        ReadOnlySpan<byte> data = icc[tag];
        if (data.Length < 20 || !data[..4].SequenceEqual("XYZ "u8))
            throw Corrupt(format, "an XYZ tag is malformed");
        return (S15Fixed16(data[8..]), S15Fixed16(data[12..]), S15Fixed16(data[16..]));
    }

    private static ToneCurve ReadCurve(ReadOnlySpan<byte> icc, Range tag, ImageFormat? format)
    {
        ReadOnlySpan<byte> data = icc[tag];
        if (data.Length < 12)
            throw Corrupt(format, "a tone curve tag is truncated");
        if (data[..4].SequenceEqual("curv"u8))
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
            if (count > (data.Length - 12) / 2)
                throw Corrupt(format, "a curve table is truncated");
            if (count == 0) return ToneCurve.Identity;
            if (count == 1) return ToneCurve.Power(BinaryPrimitives.ReadUInt16BigEndian(data[12..]) / 256.0);
            var samples = new double[count];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(12 + (2 * i))..]) / 65535.0;
            }

            return ToneCurve.Table(samples);
        }

        if (data[..4].SequenceEqual("para"u8))
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
            if (type > 4)
                throw Unsupported($"ICC parametric curve type {type} is not supported.");
            int n = ToneCurve.ParameterCount(type);
            if (data.Length < 12 + (4 * n))
                throw Corrupt(format, "a parametric curve is truncated");
            var parameters = new double[n];
            for (int i = 0; i < n; i++)
            {
                parameters[i] = S15Fixed16(data[(12 + (4 * i))..]);
            }

            if (type is 1 or 2 && parameters[1] == 0)
                throw Corrupt(format, "a parametric curve has a zero slope");
            return ToneCurve.Parametric(type, parameters);
        }

        throw Unsupported($"ICC tone curve type '{Ascii(data[..4])}' is not supported; 'curv' and 'para' are.");
    }

    private static double S15Fixed16(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32BigEndian(data) / 65536.0;

    private static string Ascii(ReadOnlySpan<byte> signature)
    {
        Span<char> chars = stackalloc char[signature.Length];
        for (int i = 0; i < signature.Length; i++)
        {
            chars[i] = signature[i] is >= 0x20 and < 0x7F ? (char)signature[i] : '?';
        }

        return new string(chars).TrimEnd();
    }

    private static ImageFormatException Corrupt(ImageFormat? format, string reason) =>
        new(format, $"Invalid ICC profile: {reason}.");

    private static NotSupportedException Unsupported(string message) => new(message);

    /// <summary>The tag directory; the first entry of each signature wins.</summary>
    private readonly ref struct TagTable(ReadOnlySpan<byte> table, int count)
    {
        private readonly ReadOnlySpan<byte> _table = table;

        public static TagTable Read(ReadOnlySpan<byte> icc, ImageFormat? format)
        {
            uint count = BinaryPrimitives.ReadUInt32BigEndian(icc[HeaderSize..]);
            if (count > (icc.Length - HeaderSize - 4) / 12)
                throw Corrupt(format, "the tag table does not fit");
            ReadOnlySpan<byte> table = icc.Slice(HeaderSize + 4, (int)count * 12);
            for (int i = 0; i < (int)count; i++)
            {
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(table[((i * 12) + 4)..]);
                uint size = BinaryPrimitives.ReadUInt32BigEndian(table[((i * 12) + 8)..]);
                if (offset < HeaderSize || offset > icc.Length || size > icc.Length - offset)
                    throw Corrupt(format, "a tag lies outside the profile");
            }

            return new TagTable(table, (int)count);
        }

        public Range? Find(ReadOnlySpan<byte> signature)
        {
            for (int i = 0; i < count; i++)
            {
                ReadOnlySpan<byte> entry = _table.Slice(i * 12, 12);
                if (entry[..4].SequenceEqual(signature))
                {
                    int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
                    int size = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
                    return offset..(offset + size);
                }
            }

            return null;
        }
    }
}
