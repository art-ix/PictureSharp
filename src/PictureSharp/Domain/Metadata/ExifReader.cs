using System.Buffers.Binary;

namespace PictureSharp.Domain.Metadata;

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
        ReadOnlySpan<byte> tiff = exif;
        if (tiff.StartsWith(JpegPrefix)) tiff = tiff[JpegPrefix.Length..];
        if (!ValidDirectory(tiff)) return false;
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

    public static byte[] SetOrientation(byte[]? exif, ExifOrientation orientation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan((int)orientation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan((int)orientation, 8);
        if (exif is null || exif.Length == 0)
        {
            byte[] minimal = new byte[26];
            "II"u8.CopyTo(minimal);
            BinaryPrimitives.WriteUInt16LittleEndian(minimal.AsSpan(2), 42);
            BinaryPrimitives.WriteUInt32LittleEndian(minimal.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt16LittleEndian(minimal.AsSpan(8), 1);
            WriteOrientationEntry(minimal.AsSpan(10, 12), orientation, false);
            return minimal;
        }
        if (!ValidDirectory(exif)) throw new ImageFormatException("Cannot edit malformed EXIF data.");
        if (TrySetOrientation(exif, orientation)) return exif;
        bool big = exif.AsSpan().StartsWith("MM"u8);
        if (exif.Length < 8 || (!big && !exif.AsSpan().StartsWith("II"u8)) || ReadUInt16(exif.AsSpan(2), big) != 42)
            throw new ImageFormatException("Cannot edit malformed EXIF data.");
        uint offset = ReadUInt32(exif.AsSpan(4), big);
        if (offset < 8 || offset > exif.Length - 2) throw new ImageFormatException("Invalid EXIF directory offset.");
        int count = ReadUInt16(exif.AsSpan((int)offset), big);
        long end = (long)offset + 2 + (12L * count);
        if (count == ushort.MaxValue || end + 4 > exif.Length) throw new ImageFormatException("Invalid EXIF directory.");
        // Do not create a duplicate if a malformed orientation entry already exists.
        if (FindEntry(exif, OrientationTag, out _) is not null) throw new ImageFormatException("Unsupported EXIF orientation entry.");
        int directory = (exif.Length + 1) & ~1;
        byte[] result = new byte[checked(directory + 2 + ((count + 1) * 12) + 4)];
        exif.CopyTo(result, 0);
        Write32(result.AsSpan(4), (uint)directory, big);
        Write16(result.AsSpan(directory), (ushort)(count + 1), big);
        int destination = directory + 2;
        bool inserted = false;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = exif.AsSpan((int)offset + 2 + (i * 12), 12);
            if (!inserted && ReadUInt16(entry, big) > OrientationTag)
            {
                WriteOrientationEntry(result.AsSpan(destination, 12), orientation, big);
                destination += 12;
                inserted = true;
            }
            entry.CopyTo(result.AsSpan(destination, 12));
            destination += 12;
        }
        if (!inserted)
        {
            WriteOrientationEntry(result.AsSpan(destination, 12), orientation, big);
            destination += 12;
        }
        exif.AsSpan((int)end, 4).CopyTo(result.AsSpan(destination, 4));
        return result;
    }

    private static bool ValidDirectory(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8) return false;
        bool big = tiff.StartsWith("MM"u8);
        if ((!big && !tiff.StartsWith("II"u8)) || ReadUInt16(tiff[2..], big) != 42) return false;
        long directory = ReadUInt32(tiff[4..], big);
        if (directory < 8 || directory + 2 > tiff.Length) return false;
        int count = ReadUInt16(tiff[(int)directory..], big);
        return directory + 2 + (12L * count) + 4 <= tiff.Length;
    }

    private static void WriteOrientationEntry(Span<byte> entry, ExifOrientation orientation, bool big)
    {
        entry.Clear();
        Write16(entry, OrientationTag, big);
        Write16(entry[2..], TypeShort, big);
        Write32(entry[4..], 1, big);
        Write16(entry[8..], (ushort)orientation, big);
    }

    private static void Write16(Span<byte> output, ushort value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt16BigEndian(output, value);
        else BinaryPrimitives.WriteUInt16LittleEndian(output, value);
    }

    private static void Write32(Span<byte> output, uint value, bool big)
    {
        if (big) BinaryPrimitives.WriteUInt32BigEndian(output, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(output, value);
    }

    // Returns the offset of the 2-byte orientation value within the TIFF stream, or -1.
    private static int FindOrientationValueOffset(ReadOnlySpan<byte> tiff, out bool bigEndian) =>
        FindEntry(tiff, OrientationTag, out bigEndian) is int entry && ReadUInt16(tiff[(entry + 2)..], bigEndian) == TypeShort && ReadUInt32(tiff[(entry + 4)..], bigEndian) == 1
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
