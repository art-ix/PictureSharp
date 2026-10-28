using System.Numerics;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Per-channel lookup tables for tonal operations, one entry per sample value: 256 bytes for 8-bit images,
/// 65536 <see cref="ushort"/> values for 16-bit ones (<c>ToneLut.Wide.cs</c>). Alpha is never changed.
/// </summary>
internal static partial class ToneLut
{
    /// <summary>Length of a table holding the red, green and blue maps back to back.</summary>
    public const int Length = 3 * 256;

    /// <summary>A table that maps every channel to itself.</summary>
    public static byte[] CreateIdentity()
    {
        byte[] lut = new byte[Length];
        for (int i = 0; i < Length; i++)
        {
            lut[i] = (byte)i;
        }

        return lut;
    }

    /// <summary>Writes <paramref name="map"/> into the parts of <paramref name="lut"/> for <paramref name="channels"/>.</summary>
    public static void SetChannels(Span<byte> lut, ReadOnlySpan<byte> map, ColorChannels channels)
    {
        for (int c = 0; c < 3; c++)
        {
            if (((int)channels & (1 << c)) != 0)
            {
                map.CopyTo(lut.Slice(c * 256, 256));
            }
        }
    }

    /// <summary>
    /// ob + (ow − ob) · clamp((x − ib) / (iw − ib), 0, 1)^(1 / γ) in codes of 0–<paramref name="max"/>, in double
    /// precision and rounded half up (tests/assets/graphics_reference.py levels_lut); <paramref name="map"/> has
    /// max + 1 entries. A level migrated from 8 bits as v / 255f is exactly v at max 255 (<see cref="NormalizedLevel"/>).
    /// </summary>
    public static void Levels<T>(Span<T> map, LevelsOptions options, int max) where T : unmanaged, IBinaryInteger<T>
    {
        double ib = NormalizedLevel.ToCodes(options.InputBlackNormalized, max);
        double range = NormalizedLevel.ToCodes(options.InputWhiteNormalized, max) - ib;
        double ob = NormalizedLevel.ToCodes(options.OutputBlackNormalized, max);
        double span = NormalizedLevel.ToCodes(options.OutputWhiteNormalized, max) - ob;
        double exponent = 1.0 / options.Gamma;
        for (int x = 0; x <= max; x++)
        {
            double t = Math.Clamp((x - ib) / range, 0.0, 1.0);
            map[x] = T.CreateTruncating((int)Math.Floor(ob + (span * Math.Pow(t, exponent)) + 0.5));
        }
    }

    /// <summary>Whether <paramref name="lut"/> maps every channel to itself.</summary>
    public static bool IsIdentity(ReadOnlySpan<byte> lut)
    {
        for (int i = 0; i < Length; i++)
        {
            if (lut[i] != (byte)i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Maps the color channels of <paramref name="pixels"/> through <paramref name="lut"/>.</summary>
    public static void Apply(Span<Rgba32> pixels, ReadOnlySpan<byte> lut, CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            ApplyBlock(pixels, lut);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        for (int offset = 0; offset < pixels.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(16384, pixels.Length - offset);
            ApplyBlock(pixels.Slice(offset, length), lut);
            offset += length;
        }
    }

    private static void ApplyBlock(Span<Rgba32> pixels, ReadOnlySpan<byte> lut)
    {
        ReadOnlySpan<byte> r = lut[..256];
        ReadOnlySpan<byte> g = lut.Slice(256, 256);
        ReadOnlySpan<byte> b = lut.Slice(512, 256);
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            pixels[i] = new Rgba32(r[p.R], g[p.G], b[p.B], p.A);
        }
    }
}
