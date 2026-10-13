using System.Buffers.Binary;

namespace WebpSharp.Formats.Metadata;

/// <summary>Minimal reader/patcher for the TIFF structure of EXIF blocks (IFD0 only).</summary>
internal static class ExifReader
{
    private const ushort OrientationTag = 0x0112;
    private const ushort TypeShort = 3;

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
    private static int FindOrientationValueOffset(ReadOnlySpan<byte> tiff, out bool bigEndian)
    {
        bigEndian = false;
        if (tiff.StartsWith(JpegPrefix))
        {
            int inner = FindOrientationValueOffset(tiff[JpegPrefix.Length..], out bigEndian);
            return inner < 0 ? -1 : inner + JpegPrefix.Length;
        }

        if (tiff.Length < 8)
        {
            return -1;
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
            return -1;
        }

        if (ReadUInt16(tiff[2..], bigEndian) != 42)
        {
            return -1;
        }

        long ifd = ReadUInt32(tiff[4..], bigEndian);
        if (ifd < 8 || ifd + 2 > tiff.Length)
        {
            return -1;
        }

        int count = ReadUInt16(tiff[(int)ifd..], bigEndian);
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + (12L * i);
            if (entry + 12 > tiff.Length)
            {
                return -1;
            }

            ReadOnlySpan<byte> e = tiff.Slice((int)entry, 12);
            if (ReadUInt16(e, bigEndian) == OrientationTag)
            {
                return ReadUInt16(e[2..], bigEndian) == TypeShort ? (int)entry + 8 : -1;
            }
        }

        return -1;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint ReadUInt32(ReadOnlySpan<byte> s, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
}
