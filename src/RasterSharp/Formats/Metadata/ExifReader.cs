using System.Buffers.Binary;

namespace RasterSharp.Formats.Metadata;

/// <summary>Minimal reader/patcher for the TIFF structure of EXIF blocks (IFD0 only).</summary>
internal static class ExifReader
{
    private const ushort OrientationTag = 0x0112;
    private const ushort XResolutionTag = 0x011A;
    private const ushort YResolutionTag = 0x011B;
    private const ushort ResolutionUnitTag = 0x0128;
    private const ushort TypeShort = 3;
    private const ushort TypeRational = 5;

    private static ReadOnlySpan<byte> JpegPrefix => "Exif\0\0"u8;

    public static byte[] StripJpegPrefix(byte[] exif) =>
        exif.AsSpan().StartsWith(JpegPrefix) ? exif[JpegPrefix.Length..] : exif;

    public static ExifOrientation GetOrientation(ReadOnlySpan<byte> exif)
    {
        int offset = FindOrientationValueOffset(exif, out bool bigEndian);
        if (offset < 0)
        {
            return ExifOrientation.Unknown;
        }

        int value = ReadUInt16(exif[offset..], bigEndian);
        return value is >= 1 and <= 8 ? (ExifOrientation)value : ExifOrientation.Unknown;
    }

    /// <summary>XResolution/YResolution of IFD0 in DPI, or null when absent or not in inches/centimeters.</summary>
    public static (float X, float Y)? GetResolution(ReadOnlySpan<byte> exif)
    {
        int unit = 2;   // inches unless stated otherwise
        if (FindEntry(exif, ResolutionUnitTag, out bool bigEndian) is int u)
        {
            if (ReadUInt16(exif[(u + 2)..], bigEndian) != TypeShort)
            {
                return null;
            }

            unit = ReadUInt16(exif[(u + 8)..], bigEndian);
        }

        if (unit is not (2 or 3) || Rational(exif, XResolutionTag) is not double x || Rational(exif, YResolutionTag) is not double y
            || !(x > 0) || !(y > 0) || double.IsInfinity(x) || double.IsInfinity(y))
        {
            return null;
        }

        double scale = unit == 3 ? 2.54 : 1;
        return ((float)(x * scale), (float)(y * scale));
    }

    private static double? Rational(ReadOnlySpan<byte> tiff, ushort tag)
    {
        if (FindEntry(tiff, tag, out bool bigEndian) is not int entry || ReadUInt16(tiff[(entry + 2)..], bigEndian) != TypeRational)
        {
            return null;
        }

        int start = tiff.StartsWith(JpegPrefix) ? JpegPrefix.Length : 0;
        long offset = start + (long)ReadUInt32(tiff[(entry + 8)..], bigEndian);
        if (offset + 8 > tiff.Length)
        {
            return null;
        }

        uint num = ReadUInt32(tiff[(int)offset..], bigEndian);
        uint den = ReadUInt32(tiff[((int)offset + 4)..], bigEndian);
        return den == 0 ? null : (double)num / den;
    }

    public static bool TrySetOrientation(Span<byte> exif, ExifOrientation orientation)
    {
        int offset = FindOrientationValueOffset(exif, out bool bigEndian);
        if (offset < 0)
        {
            return false;
        }

        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(exif[offset..], (ushort)orientation);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(exif[offset..], (ushort)orientation);
        }

        return true;
    }

    // Returns the offset of the 2-byte orientation value within the TIFF stream, or -1.
    private static int FindOrientationValueOffset(ReadOnlySpan<byte> tiff, out bool bigEndian) =>
        FindEntry(tiff, OrientationTag, out bigEndian) is int entry && ReadUInt16(tiff[(entry + 2)..], bigEndian) == TypeShort
            ? entry + 8
            : -1;

    // Returns the offset of the 12-byte IFD0 entry with the given tag, or null.
    private static int? FindEntry(ReadOnlySpan<byte> tiff, ushort tag, out bool bigEndian)
    {
        bigEndian = false;
        if (tiff.StartsWith(JpegPrefix))
        {
            int? inner = FindEntry(tiff[JpegPrefix.Length..], tag, out bigEndian);
            return inner + JpegPrefix.Length;
        }

        if (tiff.Length < 8)
        {
            return null;
        }

        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            bigEndian = false;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            bigEndian = true;
        }
        else
        {
            return null;
        }

        if (ReadUInt16(tiff[2..], bigEndian) != 42)
        {
            return null;
        }

        long ifd = ReadUInt32(tiff[4..], bigEndian);
        if (ifd < 8 || ifd + 2 > tiff.Length)
        {
            return null;
        }

        int count = ReadUInt16(tiff[(int)ifd..], bigEndian);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + (12L * i);
            if (entry + 12 > tiff.Length)
            {
                return null;
            }

            if (ReadUInt16(tiff[(int)entry..], bigEndian) == tag)
            {
                return (int)entry;
            }
        }

        return null;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint ReadUInt32(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
