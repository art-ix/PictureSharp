using System.Drawing;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Geometry
{
    /// <summary>A copy of <paramref name="rect"/> of the frame at its storage depth.</summary>
    public readonly struct CropFrame(ImageFrame frame, Rectangle rect) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            PixelBuffer.From(Crop(frame.Buffer.As<TPixel>(), frame.Width, rect));
    }

    /// <summary>The frame rotated by an odd number of quarter turns into a new buffer.</summary>
    public readonly struct RotateQuarterFrame(ImageFrame frame, int rotation, bool flipX, CancellationToken cancellationToken)
        : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            PixelBuffer.From(RotateQuarterPixels(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, rotation, flipX, cancellationToken));
    }

    /// <summary>Even quarter turns and flips in place.</summary>
    public readonly struct RotateFlipInPlace(ImageFrame frame, int rotation, bool flipX) : IPixelOperation<bool>
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            RotateFlip(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, rotation, flipX);
            return true;
        }
    }

    /// <summary>
    /// The frame placed on a <paramref name="width"/> x <paramref name="height"/> canvas filled with
    /// <paramref name="fill"/>, which is converted exactly to the storage depth.
    /// </summary>
    public readonly struct PadFrame(ImageFrame frame, int width, int height, Point offset, Rgba64 fill) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            var canvas = GC.AllocateUninitializedArray<TPixel>(width * height);
            canvas.AsSpan().Fill(TPixel.FromRgba64(fill));
            Place<TPixel>(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, canvas, width, height, offset.X, offset.Y);
            return PixelBuffer.From(canvas);
        }
    }
}
