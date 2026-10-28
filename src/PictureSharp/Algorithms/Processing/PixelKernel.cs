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
