using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// An in-place operation on a frame's pixels with a dedicated 8-bit implementation (usually a byte-exact port) and a
/// high-precision form for the other depths.
/// </summary>
internal interface IPixelKernel
{
    void Run(Rgba32[] pixels);

    void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>;
}

/// <summary>Runs an <see cref="IPixelKernel"/> on one frame through <see cref="PixelDispatch"/>.</summary>
internal readonly struct PixelKernelFrame<TKernel>(ImageFrame frame, TKernel kernel) : IPixelOperation<bool>
    where TKernel : struct, IPixelKernel
{
    public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            kernel.Run(frame.Buffer.As<Rgba32>());
        }
        else
        {
            kernel.RunWide(frame.Buffer.As<TPixel>());
        }

        return true;
    }
}

/// <summary>A filter from a frame's pixels into a new buffer of the same size and pixel type.</summary>
internal interface ILocalFilter
{
    void Run<TPixel>(TPixel[] source, TPixel[] destination, int width, int height, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelOps<TPixel>;
}

/// <summary>Runs an <see cref="ILocalFilter"/> on one frame into a new buffer through <see cref="PixelDispatch"/>.</summary>
internal readonly struct LocalFilterFrame<TFilter>(ImageFrame frame, TFilter filter, CancellationToken cancellationToken) : IPixelOperation<PixelBuffer>
    where TFilter : struct, ILocalFilter
{
    public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
    {
        TPixel[] result = GC.AllocateUninitializedArray<TPixel>(frame.Width * frame.Height);
        filter.Run(frame.Buffer.As<TPixel>(), result, frame.Width, frame.Height, cancellationToken);
        return PixelBuffer.From(result);
    }
}
