using System.Buffers.Binary;
using System.Text;

namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>
/// Writes the ICC v4.3 display profiles libultrahdr v2.0.2 stores with a gain map: sRGB or Display P3 primaries with the
/// sRGB transfer function. libultrahdr recognizes the map's color space only from these exact colorant values.
/// </summary>
internal static class IccProfileWriter
{
    private const int HeaderSize = 132;   // the header and the tag count
    private const int TagTableEntrySize = 12;

    // port: lib/include/ultrahdr/icc.h kSRGB (skcms 16.16 values) and kDisplayP3, columns are the r, g, b colorants
    private static ReadOnlySpan<int> SrgbFixed => [0x6FA2, 0x6299, 0x24A0, 0x38F5, 0xB785, 0x0F84, 0x0390, 0x18DA, 0xB6CF];

    private static ReadOnlySpan<float> DisplayP3 =>
        [0.515102f, 0.291965f, 0.157153f, 0.241182f, 0.692236f, 0.0665819f, -0.00104941f, 0.0418818f, 0.784378f];

    // port: lib/include/ultrahdr/icc.h kSRGB_TransFun (g, a, b, c, d, e, f)
    private static ReadOnlySpan<float> SrgbTransfer => [2.4f, (float)(1 / 1.055), (float)(0.055 / 1.055), (float)(1 / 12.92), 0.04045f, 0.0f, 0.0f];

    /// <summary>The profile (without the JPEG APP2 identifier) for <paramref name="space"/>.</summary>
    // port: lib/src/icc.cpp IccHelper::writeIccProfile(UHDR_CT_SRGB, UHDR_CG_BT_709 or UHDR_CG_DISPLAY_P3)
    public static byte[] WriteSrgbTransfer(HdrColorSpace space)
    {
        Span<int> matrix = stackalloc int[9];
        if (space == HdrColorSpace.LinearDisplayP3)
        {
            for (int i = 0; i < 9; i++) matrix[i] = FloatRoundToFixed(DisplayP3[i]);
        }
        else
        {
            SrgbFixed.CopyTo(matrix);
        }

        string gamut = space == HdrColorSpace.LinearDisplayP3 ? "Display P3" : "sRGB";
        byte[] trc = ParaTag();
        (string Tag, byte[] Data)[] tags =
        [
            ("desc", TextTag($"{gamut} Gamut with sRGB Transfer")),
            ("rXYZ", XyzTag(matrix[0], matrix[3], matrix[6])),
            ("gXYZ", XyzTag(matrix[1], matrix[4], matrix[7])),
            ("bXYZ", XyzTag(matrix[2], matrix[5], matrix[8])),
            ("wtpt", XyzTag(FloatRoundToFixed(0.9642f), FloatRoundToFixed(1.0000f), FloatRoundToFixed(0.8249f))),
            ("rTRC", trc),
            ("gTRC", trc),
            ("bTRC", trc),
            ("cprt", TextTag("Google Inc. 2022")),
        ];

        int tableSize = TagTableEntrySize * tags.Length;
        int size = HeaderSize + tableSize + tags.Sum(t => t.Data.Length);
        byte[] profile = new byte[size];
        Span<byte> h = profile;
        BinaryPrimitives.WriteUInt32BigEndian(h, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(h[8..], 0x04300000);   // version 4.3
        "mntrRGB XYZ "u8.CopyTo(h[12..]);
        "acsp"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32BigEndian(h[64..], 1);           // relative colorimetric
        BinaryPrimitives.WriteInt32BigEndian(h[68..], FloatRoundToFixed(0.9642f));
        BinaryPrimitives.WriteInt32BigEndian(h[72..], FloatRoundToFixed(1.0000f));
        BinaryPrimitives.WriteInt32BigEndian(h[76..], FloatRoundToFixed(0.8249f));
        BinaryPrimitives.WriteUInt32BigEndian(h[128..], (uint)tags.Length);

        int offset = HeaderSize + tableSize, entry = HeaderSize;
        foreach ((string tag, byte[] data) in tags)
        {
            Encoding.ASCII.GetBytes(tag, h[entry..]);
            BinaryPrimitives.WriteUInt32BigEndian(h[(entry + 4)..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(h[(entry + 8)..], (uint)data.Length);
            data.CopyTo(h[offset..]);
            entry += TagTableEntrySize;
            offset += data.Length;
        }

        return profile;
    }

    // port: lib/include/ultrahdr/icc.h float_round_to_fixed, float_saturate2int
    private static int FloatRoundToFixed(float x)
    {
        const float MaxS32FitsInFloat = 2147483520;
        float v = (float)Math.Floor(((double)x * 65536) + 0.5);
        v = v < MaxS32FitsInFloat ? v : MaxS32FitsInFloat;
        v = v > -MaxS32FitsInFloat ? v : -MaxS32FitsInFloat;
        return (int)v;
    }

    // port: lib/src/icc.cpp IccHelper::write_text_tag (multiLocalizedUnicodeType, en-US, UTF-16BE)
    private static byte[] TextTag(string text)
    {
        byte[] data = new byte[((28 + (2 * text.Length) + 2) >> 2) << 2];
        "mluc"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 12);
        "enUS"u8.CopyTo(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)(2 * text.Length));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        for (int i = 0; i < text.Length; i++)
        {
            data[28 + (2 * i) + 1] = (byte)text[i];
        }

        return data;
    }

    // port: lib/src/icc.cpp IccHelper::write_xyz_tag
    private static byte[] XyzTag(int x, int y, int z)
    {
        byte[] data = new byte[20];
        "XYZ "u8.CopyTo(data);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), x);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), y);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), z);
        return data;
    }

    // port: lib/src/icc.cpp IccHelper::write_trc_tag(const TransferFunction&), the kGABCDEF_ParaCurveType branch
    private static byte[] ParaTag()
    {
        byte[] data = new byte[40];
        "para"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), 4);   // function type 4, then two reserved bytes
        for (int i = 0; i < 7; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + (4 * i)), FloatRoundToFixed(SrgbTransfer[i]));
        }

        return data;
    }
}
