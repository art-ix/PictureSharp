namespace PictureSharp.Domain.Pixels;

/// <summary>An algorithm with one generic body for every storage pixel type; state lives in the struct's fields.</summary>
internal interface IPixelOperation<TResult>
{
    TResult Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>;
}

/// <summary>
/// The single dispatch from <see cref="PixelType"/> to a typed algorithm, once per frame or image; operations never
/// switch on the pixel type themselves. An operation whose 8-bit path is a byte-exact port keeps that path by
/// testing <c>typeof(TPixel) == typeof(Rgba32)</c> inside <c>Run</c>, which the JIT folds.
/// </summary>
internal static class PixelDispatch
{
    public static TResult Run<TOperation, TResult>(PixelType pixelType, ref TOperation operation)
        where TOperation : struct, IPixelOperation<TResult> => pixelType switch
        {
            PixelType.Rgba32 => operation.Run<Rgba32>(),
            PixelType.Rgba64 => operation.Run<Rgba64>(),
            _ => throw new ArgumentOutOfRangeException(nameof(pixelType), pixelType, "Unknown pixel type."),
        };
}
