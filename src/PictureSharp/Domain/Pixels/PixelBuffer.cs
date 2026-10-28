using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PictureSharp.Domain.Pixels;

/// <summary>
/// The pixel array of a frame: exactly one of <see cref="Rgba32"/> or <see cref="Rgba64"/> storage. Typed fields keep
/// the 8-bit access free of casts.
/// </summary>
internal readonly struct PixelBuffer
{
    private readonly Rgba32[]? _rgba32;
    private readonly Rgba64[]? _rgba64;

    private PixelBuffer(Rgba32[]? rgba32, Rgba64[]? rgba64)
    {
        _rgba32 = rgba32;
        _rgba64 = rgba64;
    }

    public bool IsEmpty => _rgba32 is null && _rgba64 is null;

    public PixelType PixelType => _rgba64 is null ? PixelType.Rgba32 : PixelType.Rgba64;

    public int Length => _rgba32?.Length ?? _rgba64?.Length ?? 0;

    /// <summary>The 8-bit array, or null for 16-bit storage.</summary>
    public Rgba32[]? Rgba32 => _rgba32;

    /// <summary>The 16-bit array, or null for 8-bit storage.</summary>
    public Rgba64[]? Rgba64 => _rgba64;

    public static implicit operator PixelBuffer(Rgba32[] pixels) => new(pixels ?? throw new ArgumentNullException(nameof(pixels)), null);

    public static implicit operator PixelBuffer(Rgba64[] pixels) => new(null, pixels ?? throw new ArgumentNullException(nameof(pixels)));

    /// <summary>Wraps a typed array; <typeparamref name="TPixel"/> must be a storage pixel type.</summary>
    public static PixelBuffer From<TPixel>(TPixel[] pixels) where TPixel : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pixels);
        return PixelTypes.Of<TPixel>() == PixelType.Rgba32
            ? new(Unsafe.As<Rgba32[]>(pixels), null)
            : new(null, Unsafe.As<Rgba64[]>(pixels));
    }

    public static PixelBuffer Allocate(PixelType pixelType, int length, bool clear) => pixelType switch
    {
        PixelType.Rgba32 => clear ? new Rgba32[length] : GC.AllocateUninitializedArray<Rgba32>(length),
        _ => clear ? new Rgba64[length] : GC.AllocateUninitializedArray<Rgba64>(length),
    };

    /// <summary>The array as <typeparamref name="TPixel"/>[], after checking that it stores that type.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public TPixel[] As<TPixel>() where TPixel : unmanaged
    {
        PixelType requested = PixelTypes.Of<TPixel>();
        object? array = requested == PixelType.Rgba32 ? _rgba32 : _rgba64;
        return array is not null ? Unsafe.As<TPixel[]>(array) : throw PixelTypes.Mismatch(PixelType, typeof(TPixel));
    }

    public Span<byte> AsBytes() => _rgba32 is not null
        ? MemoryMarshal.AsBytes(_rgba32.AsSpan())
        : MemoryMarshal.AsBytes(_rgba64.AsSpan());

    public PixelBuffer Clone() => new((Rgba32[]?)_rgba32?.Clone(), (Rgba64[]?)_rgba64?.Clone());
}
