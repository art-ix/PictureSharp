using System.Runtime.CompilerServices;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Straight ↔ premultiplied alpha as Pillow's RGBA ↔ RGBa conversions, so filters match Pillow run on "RGBa".</summary>
internal static partial class Premultiplied
{
    // port: Pillow src/libImaging/Convert.c rgbA2rgba (MULDIV255)
    public static void FromStraight(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst)
    {
        int done = FromStraightSimd(src, dst);
        FromStraightScalar(src[done..], dst[done..]);
    }

    /// <summary>Scalar <see cref="FromStraight"/>; the vector path is in <c>Premultiplied.Simd.cs</c>.</summary>
    internal static void FromStraightScalar(ReadOnlySpan<Rgba32> src, Span<Rgba32> dst)
    {
        for (int i = 0; i < src.Length; i++)
        {
            Rgba32 p = src[i];
            dst[i] = new Rgba32(MulDiv255(p.R, p.A), MulDiv255(p.G, p.A), MulDiv255(p.B, p.A), p.A);
        }
    }

    // port: Pillow src/libImaging/Convert.c rgba2rgbA
    public static void ToStraight(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            // Opaque and fully transparent pixels stay as they are; skip runs of them.
            i += CountUnchanged(pixels[i..]);
            if (i >= pixels.Length)
            {
                break;
            }

            Rgba32 p = pixels[i];
            int a = p.A;
            if (a != 0 && a != 255)
            {
                pixels[i] = new Rgba32(
                    (byte)Math.Min(255, 255 * p.R / a),
                    (byte)Math.Min(255, 255 * p.G / a),
                    (byte)Math.Min(255, 255 * p.B / a),
                    p.A);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte MulDiv255(int a, int b)
    {
        int tmp = (a * b) + 128;
        return (byte)(((tmp >> 8) + tmp) >> 8);
    }
}
