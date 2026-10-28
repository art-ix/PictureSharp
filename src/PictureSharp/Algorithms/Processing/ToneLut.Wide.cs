using System.Buffers;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class ToneLut
{
    /// <summary>Entries of one 16-bit channel map: one per sample value.</summary>
    public const int Channel16 = ushort.MaxValue + 1;

    /// <summary>Length of a 16-bit table holding the red, green and blue maps back to back (384 KB).</summary>
    public const int Length16 = 3 * Channel16;

    /// <summary>A rented 16-bit table (at least <see cref="Length16"/> long) that maps every channel to itself; return it with <see cref="Return16"/>.</summary>
    public static ushort[] RentIdentity16()
    {
        ushort[] lut = ArrayPool<ushort>.Shared.Rent(Length16);
        for (int c = 0; c < 3; c++)
        {
            Span<ushort> map = lut.AsSpan(c * Channel16, Channel16);
            for (int v = 0; v < map.Length; v++)
            {
                map[v] = (ushort)v;
            }
        }

        return lut;
    }

    public static void Return16(ushort[] lut) => ArrayPool<ushort>.Shared.Return(lut);

    /// <summary>Writes <paramref name="map"/> into the parts of the 16-bit <paramref name="lut"/> for <paramref name="channels"/>.</summary>
    public static void SetChannels(Span<ushort> lut, ReadOnlySpan<ushort> map, ColorChannels channels)
    {
        for (int c = 0; c < 3; c++)
        {
            if (((int)channels & (1 << c)) != 0)
            {
                map.CopyTo(lut.Slice(c * Channel16, Channel16));
            }
        }
    }

    /// <summary>Whether the 16-bit <paramref name="lut"/> maps every channel to itself.</summary>
    public static bool IsIdentity(ReadOnlySpan<ushort> lut)
    {
        for (int i = 0; i < Length16; i++)
        {
            if (lut[i] != (ushort)i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Maps the color channels of <paramref name="pixels"/> through the 16-bit <paramref name="lut"/>.</summary>
    public static void Apply(Span<Rgba64> pixels, ReadOnlySpan<ushort> lut, CancellationToken cancellationToken = default)
    {
        ReadOnlySpan<ushort> r = lut[..Channel16];
        ReadOnlySpan<ushort> g = lut.Slice(Channel16, Channel16);
        ReadOnlySpan<ushort> b = lut.Slice(2 * Channel16, Channel16);
        for (int offset = 0; offset < pixels.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(16384, pixels.Length - offset);
            Span<Rgba64> block = pixels.Slice(offset, length);
            for (int i = 0; i < block.Length; i++)
            {
                Rgba64 p = block[i];
                block[i] = new Rgba64(r[p.R], g[p.G], b[p.B], p.A);
            }

            offset += length;
        }
    }
}
