using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Channel access of the planar cleanup filters (<see cref="GuidedFilter"/>, <see cref="ChromaFilter"/>):
/// premultiplied values in [0, 1] at the precision of the storage depth. The 8-bit forms keep the filters' original
/// arithmetic.
/// </summary>
internal static class PremultipliedChannel
{
    private const double Max16Squared = 65535.0 * 65535.0;

    /// <summary>Red (0), green (1) or blue (2) times alpha, both divided by the channel maximum.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Load<TPixel>(TPixel p, int channel) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Rgba32 q = Unsafe.As<TPixel, Rgba32>(ref p);
            int value = channel switch { 0 => q.R, 1 => q.G, _ => q.B };
            return value * q.A / 65025f;
        }

        Rgba64 w = TPixel.ToRgba64(p);
        int wide = channel switch { 0 => w.R, 1 => w.G, _ => w.B };
        return (float)((double)wide * w.A / Max16Squared);
    }

    /// <summary>The 16-bit straight value of premultiplied <paramref name="premultiplied"/> under alpha <paramref name="alpha"/> (not 0), clamped and rounded half up.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Store16(float premultiplied, int alpha)
    {
        double v = premultiplied * Max16Squared / alpha;
        return v <= 0 ? (ushort)0 : v >= ushort.MaxValue ? ushort.MaxValue : (ushort)(int)(v + 0.5);
    }

    /// <summary>The 16-bit pixel with channel <paramref name="channel"/> replaced by <paramref name="value"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rgba64 With(Rgba64 p, int channel, ushort value) => channel switch
    {
        0 => new Rgba64(value, p.G, p.B, p.A),
        1 => new Rgba64(p.R, value, p.B, p.A),
        _ => new Rgba64(p.R, p.G, value, p.A),
    };
}
