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

    /// <summary>The largest absolute difference of any channel, alpha included, at the storage depth.</summary>
    static abstract int MaxChannelDifference(TSelf x, TSelf y);
}
