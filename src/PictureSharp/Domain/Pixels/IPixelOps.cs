using System.Numerics;

namespace PictureSharp.Domain.Pixels;

/// <summary>
/// The internal contract of a storage pixel type, used by algorithms that <see cref="PixelDispatch"/> runs once per
/// frame. <see cref="Rgba32"/> and <see cref="Rgba64"/> implement it explicitly, so it never becomes public surface.
/// </summary>
internal interface IPixelOps<TSelf> where TSelf : unmanaged, IPixelOps<TSelf>
{
    static abstract PixelType PixelType { get; }

    /// <summary>The value of an opaque alpha or a full channel (255 or 65535).</summary>
    static abstract int MaxValue { get; }

    static abstract TSelf FromRgba64(Rgba64 pixel);

    static abstract Rgba64 ToRgba64(TSelf pixel);

    /// <summary>Converts an 8-bit value exactly (widening for 16-bit storage).</summary>
    static abstract TSelf FromRgba32(Rgba32 pixel);

    static abstract int Alpha(TSelf pixel);

    /// <summary>The pixel with alpha <paramref name="alpha"/> (0 to <see cref="MaxValue"/>) and its color unchanged.</summary>
    static abstract TSelf WithAlpha(TSelf pixel, int alpha);

    /// <summary>BT.601 luma at the storage depth, with the weights of <see cref="PixelConverter.Luma"/>.</summary>
    static abstract int Luma(TSelf pixel);

    /// <summary>The channels divided by <see cref="MaxValue"/> (correctly rounded, so 8-bit values and their exact
    /// 16-bit widenings give the same floats).</summary>
    static abstract Vector4 ToUnit(TSelf pixel);

    /// <summary>
    /// The channels of <paramref name="unit"/> clamped to [0, 1] and rounded half up to the storage depth; NaN gives 0.
    /// </summary>
    static abstract TSelf FromUnit(Vector4 unit);

    /// <summary>The largest absolute difference of any channel, alpha included, at the storage depth.</summary>
    static abstract int MaxChannelDifference(TSelf x, TSelf y);
}
