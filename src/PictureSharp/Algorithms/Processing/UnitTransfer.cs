using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// The working space of the high-precision paths: pixels as floats in [0, 1], straight or premultiplied, with the
/// colors either as stored (<see cref="EncodedUnit"/>) or decoded to linear light (<see cref="LinearUnit"/>). Values
/// are rounded only when stored back, at the depth of the destination.
/// </summary>
internal interface IUnitTransfer
{
    /// <summary>A straight-alpha pixel in the working space.</summary>
    static abstract Vector4 Load<TPixel>(TPixel pixel) where TPixel : unmanaged, IPixelOps<TPixel>;

    /// <summary>A straight-alpha working value at the depth of <typeparamref name="TPixel"/>.</summary>
    static abstract TPixel Store<TPixel>(Vector4 straight) where TPixel : unmanaged, IPixelOps<TPixel>;

    /// <summary>
    /// A premultiplied working value back to a straight-alpha pixel; fully transparent results become transparent
    /// black. Each space unpremultiplies like the 8-bit path it extends.
    /// </summary>
    static abstract TPixel StorePremultiplied<TPixel>(Vector4 premultiplied) where TPixel : unmanaged, IPixelOps<TPixel>;

    /// <summary>A value between separable passes: clipped like the 8-bit pass would clip it, never rounded.</summary>
    static abstract Vector4 Intermediate(Vector4 value);
}

/// <summary>Stored (encoded) values; unpremultiplication clips every channel first, as Pillow's 8-bit passes do.</summary>
internal readonly struct EncodedUnit : IUnitTransfer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Load<TPixel>(TPixel pixel) where TPixel : unmanaged, IPixelOps<TPixel> => TPixel.ToUnit(pixel);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel Store<TPixel>(Vector4 straight) where TPixel : unmanaged, IPixelOps<TPixel> => TPixel.FromUnit(straight);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel StorePremultiplied<TPixel>(Vector4 premultiplied) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        Vector4 v = Vector4.Clamp(premultiplied, Vector4.Zero, Vector4.One);
        if (PixelDepth.Quantize(v.W, TPixel.MaxValue) == 0)
        {
            return default;
        }

        return TPixel.FromUnit(new Vector4(new Vector3(v.X, v.Y, v.Z) / v.W, v.W));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Intermediate(Vector4 value) => Vector4.Clamp(value, Vector4.Zero, Vector4.One);
}

/// <summary>
/// Linear-light sRGB through continuous transfer functions; unpremultiplication divides by the unclipped alpha, as
/// the 8-bit linear-light resize does.
/// </summary>
internal readonly struct LinearUnit : IUnitTransfer
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Load<TPixel>(TPixel pixel) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        // Decode16 of an exact widening equals the 8-bit decode, so both depths share one table.
        Rgba64 p = TPixel.ToRgba64(pixel);
        return new(SrgbTransfer.Decode16(p.R), SrgbTransfer.Decode16(p.G), SrgbTransfer.Decode16(p.B), p.A / 65535f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel Store<TPixel>(Vector4 straight) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            // The 8-bit encoder table resolves linear light in 65536 steps, within 0.05 of an 8-bit code.
            var narrow = new Rgba32(SrgbTransfer.Encode(straight.X), SrgbTransfer.Encode(straight.Y), SrgbTransfer.Encode(straight.Z),
                (byte)PixelDepth.Quantize(straight.W, byte.MaxValue));
            return Unsafe.As<Rgba32, TPixel>(ref narrow);
        }

        return TPixel.FromUnit(new Vector4(
            SrgbTransfer.EncodeUnit(straight.X), SrgbTransfer.EncodeUnit(straight.Y), SrgbTransfer.EncodeUnit(straight.Z), straight.W));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TPixel StorePremultiplied<TPixel>(Vector4 premultiplied) where TPixel : unmanaged, IPixelOps<TPixel>
    {
        float w = premultiplied.W;
        if (PixelDepth.Quantize(w, TPixel.MaxValue) == 0)
        {
            return default;
        }

        return Store<TPixel>(new Vector4(new Vector3(premultiplied.X, premultiplied.Y, premultiplied.Z) / w, w));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector4 Intermediate(Vector4 value) => value;
}

/// <summary>Row conversions between pixels and premultiplied working values.</summary>
internal static class UnitRows
{
    public static void Premultiply<TPixel, TTransfer>(ReadOnlySpan<TPixel> src, Span<Vector4> dst)
        where TPixel : unmanaged, IPixelOps<TPixel>
        where TTransfer : struct, IUnitTransfer
    {
        dst = dst[..src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            Vector4 v = TTransfer.Load(src[i]);
            dst[i] = new Vector4(new Vector3(v.X, v.Y, v.Z) * v.W, v.W);
        }
    }

    public static void Unpremultiply<TPixel, TTransfer>(ReadOnlySpan<Vector4> src, Span<TPixel> dst)
        where TPixel : unmanaged, IPixelOps<TPixel>
        where TTransfer : struct, IUnitTransfer
    {
        dst = dst[..src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = TTransfer.StorePremultiplied<TPixel>(src[i]);
        }
    }

    /// <summary>Straight-alpha working values of a row.</summary>
    public static void Load<TPixel, TTransfer>(ReadOnlySpan<TPixel> src, Span<Vector4> dst)
        where TPixel : unmanaged, IPixelOps<TPixel>
        where TTransfer : struct, IUnitTransfer
    {
        dst = dst[..src.Length];
        for (int i = 0; i < src.Length; i++)
        {
            dst[i] = TTransfer.Load(src[i]);
        }
    }
}
