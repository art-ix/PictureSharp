using System.Runtime.CompilerServices;

namespace PictureSharp.Domain.Pixels;

/// <summary>Validation and sizes of the storage pixel types.</summary>
internal static class PixelTypes
{
    /// <summary>
    /// Maps <typeparamref name="TPixel"/> to its storage type. Every public typed entry point validates through here
    /// before reinterpreting memory; the comparisons fold per value-type instantiation in the JIT and Native AOT.
    /// </summary>
    /// <exception cref="NotSupportedException"><typeparamref name="TPixel"/> is not a storage pixel type.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PixelType Of<TPixel>() where TPixel : unmanaged
    {
        if (typeof(TPixel) == typeof(Rgba32))
            return PixelType.Rgba32;
        if (typeof(TPixel) == typeof(Rgba64))
            return PixelType.Rgba64;
        throw Unsupported(typeof(TPixel));
    }

    private static NotSupportedException Unsupported(Type type) =>
        new($"{type.Name} is not a pixel storage type; use {nameof(Rgba32)} or {nameof(Rgba64)}.");

    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pixelType"/> is not defined.</exception>
    public static PixelType Check(PixelType pixelType, string paramName = "pixelType") =>
        pixelType is PixelType.Rgba32 or PixelType.Rgba64
            ? pixelType
            : throw new ArgumentOutOfRangeException(paramName, pixelType, "Unknown pixel type.");

    public static int BytesPerPixel(PixelType pixelType) => pixelType == PixelType.Rgba64 ? 8 : 4;

    /// <summary>The charge of one pixel against <see cref="DecoderOptions.MaxPixels"/>: 2 for 16-bit storage.</summary>
    public static int Weight(PixelType pixelType) => pixelType == PixelType.Rgba64 ? 2 : 1;

    /// <summary>Whether <paramref name="color"/> is opaque once stored at <paramref name="pixelType"/>'s depth.</summary>
    public static bool IsOpaque(Rgba64 color, PixelType pixelType) =>
        pixelType == PixelType.Rgba32 ? Rgba64.Narrow(color.A) == byte.MaxValue : color.A == ushort.MaxValue;

    /// <summary>Throws for an operation without a 16-bit implementation yet, instead of rounding to 8 bits silently.</summary>
    public static NotSupportedException NotYetSupported() =>
        new("This operation does not support 16-bit (Rgba64) images yet; convert with ConvertPixelType(PixelType.Rgba32) first.");

    public static InvalidOperationException Mismatch(PixelType actual, Type requested) =>
        new($"The pixels are stored as {actual}, not {requested.Name}; check PixelType before typed access.");
}
