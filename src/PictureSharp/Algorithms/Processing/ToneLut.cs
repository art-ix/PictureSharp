using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Per-channel 256-entry lookup tables for tonal operations; alpha is never changed.</summary>
internal static class ToneLut
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
    /// ob + (ow − ob) · clamp((x − ib) / (iw − ib), 0, 1)^(1 / γ) in double precision, rounded half up
    /// (tests/assets/graphics_reference.py levels_lut).
    /// </summary>
    public static void Levels(Span<byte> map, LevelsOptions options)
    {
        double ib = options.InputBlack;
        double range = options.InputWhite - options.InputBlack;
        double ob = options.OutputBlack;
        double span = options.OutputWhite - options.OutputBlack;
        double exponent = 1.0 / options.Gamma;
        for (int x = 0; x < 256; x++)
        {
            double t = Math.Clamp((x - ib) / range, 0.0, 1.0);
            map[x] = (byte)(int)Math.Floor(ob + (span * Math.Pow(t, exponent)) + 0.5);
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
