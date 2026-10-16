using System.Buffers.Binary;

namespace PictureSharp.Infrastructure.Codecs.Jpeg;

/// <summary>Updates an MPF directory after the primary image's marker segments changed length.</summary>
internal static class JpegMpf
{
    /// <summary>
    /// Rewrites the MP entries in <paramref name="file"/>: the primary image's size becomes
    /// <paramref name="newPrimaryEnd"/>, and secondary images, which follow the primary image and moved with it, get
    /// offsets relative to the MPF header at its new position. Offsets are relative to the TIFF header of the segment.
    /// </summary>
    /// <param name="file">The rewritten file.</param>
    /// <param name="newTiff">Position of the MPF TIFF header in <paramref name="file"/>.</param>
    /// <param name="oldTiff">Position of the MPF TIFF header in the original file.</param>
    /// <param name="oldPrimaryEnd">End of the primary image (after EOI) in the original file.</param>
    /// <param name="newPrimaryEnd">End of the primary image in <paramref name="file"/>.</param>
    public static void Relocate(byte[] file, int newTiff, int oldTiff, int oldPrimaryEnd, int newPrimaryEnd)
    {
        Span<byte> tiff = file.AsSpan(newTiff);
        bool big = tiff[0] == 'M';
        uint ifd = Read32(tiff[4..], big);
        int count = Read16(tiff[(int)ifd..], big);
        for (int i = 0; i < count; i++)
        {
            Span<byte> entry = tiff.Slice((int)ifd + 2 + (12 * i), 12);
            if (Read16(entry, big) != 0xB002)
            {
                continue;
            }

            uint bytes = Read32(entry[4..], big), offset = Read32(entry[8..], big);
            for (int e = 0; e < bytes / 16; e++)
            {
                Span<byte> mp = tiff.Slice((int)offset + (16 * e), 16);
                uint start = Read32(mp[8..], big);
                if (start == 0)
                {
                    Write32(mp[4..], (uint)newPrimaryEnd, big);
                    continue;
                }

                long oldAbsolute = oldTiff + (long)start;
                if (oldAbsolute < oldPrimaryEnd)
                {
                    throw new NotSupportedException("An MPF image inside the primary image cannot be relocated.");
                }

                Write32(mp[8..], checked((uint)(oldAbsolute - oldPrimaryEnd + newPrimaryEnd - newTiff)), big);
            }
        }
    }

    private static ushort Read16(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);

    private static uint Read32(ReadOnlySpan<byte> s, bool big) => big ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);

    private static void Write32(Span<byte> s, uint value, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s, value);
        }
    }
}
