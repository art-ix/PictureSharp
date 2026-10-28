using System.Drawing;
using PictureSharp.Domain.Pixels;
using PictureSharp.Processing;

namespace PictureSharp.Algorithms.Processing;

internal static partial class AlphaMask
{
    /// <summary>
    /// ApplyMask on one frame: two 8-bit buffers use <see cref="Apply"/>; otherwise <see cref="ApplyWide"/> reads the
    /// mask at its own depth.
    /// </summary>
    public readonly struct ApplyFrame(ImageFrame frame, PixelBuffer mask, int maskWidth, int maskHeight, Point at, MaskOptions options,
        CancellationToken cancellationToken) : IPixelOperation<bool>
    {
        public bool Run<TFrame>() where TFrame : unmanaged, IPixelOps<TFrame>
        {
            var operation = new ApplyMaskOf<TFrame>(frame, mask, maskWidth, maskHeight, at, options, cancellationToken);
            return PixelDispatch.Run<ApplyMaskOf<TFrame>, bool>(mask.PixelType, ref operation);
        }
    }

    private readonly struct ApplyMaskOf<TFrame>(ImageFrame frame, PixelBuffer mask, int maskWidth, int maskHeight, Point at, MaskOptions options,
        CancellationToken cancellationToken) : IPixelOperation<bool>
        where TFrame : unmanaged, IPixelOps<TFrame>
    {
        public bool Run<TMask>() where TMask : unmanaged, IPixelOps<TMask>
        {
            if (typeof(TFrame) == typeof(Rgba32) && typeof(TMask) == typeof(Rgba32))
            {
                Apply(frame, mask.As<Rgba32>(), maskWidth, maskHeight, at, options, cancellationToken: cancellationToken);
            }
            else
            {
                ApplyWide(frame.Buffer.As<TFrame>(), frame.Width, frame.Height, mask.As<TMask>(), maskWidth, maskHeight, at, options, cancellationToken);
            }

            return true;
        }
    }

    /// <summary>
    /// <see cref="Apply"/> for any depths: A' = round(A · v / M) at the frame's depth, where v is the mask value at the
    /// mask's depth and M its maximum, computed exactly in integers.
    /// </summary>
    public static void ApplyWide<TFrame, TMask>(TFrame[] pixels, int width, int height, TMask[] mask, int maskWidth, int maskHeight, Point at,
        MaskOptions options, CancellationToken cancellationToken = default)
        where TFrame : unmanaged, IPixelOps<TFrame>
        where TMask : unmanaged, IPixelOps<TMask>
    {
        int x0 = Math.Clamp(at.X, 0, width);
        int y0 = Math.Clamp(at.Y, 0, height);
        int x1 = (int)Math.Clamp((long)at.X + maskWidth, 0, width);
        int y1 = (int)Math.Clamp((long)at.Y + maskHeight, 0, height);
        bool clearOutside = options.Outside == MaskOutside.Transparent;
        bool luminance = options.Channel == MaskChannel.Luminance;
        int max = TMask.MaxValue;
        int flip = options.Invert ? max : 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<TFrame> row = pixels.AsSpan(y * width, width);
            if (y < y0 || y >= y1 || x0 >= x1)
            {
                if (clearOutside)
                {
                    ClearAlpha(row);
                }

                continue;
            }

            if (clearOutside)
            {
                ClearAlpha(row[..x0]);
                ClearAlpha(row[x1..]);
            }

            ReadOnlySpan<TMask> maskRow = mask.AsSpan(((y - at.Y) * maskWidth) + (x0 - at.X), x1 - x0);
            Span<TFrame> target = row[x0..x1];
            for (int x = 0; x < target.Length; x++)
            {
                TMask m = maskRow[x];
                long v = (luminance ? TMask.Luma(m) : TMask.Alpha(m)) ^ flip;
                // M is odd, so an exact half never occurs.
                int a = (int)(((TFrame.Alpha(target[x]) * v * 2) + max) / (2L * max));
                target[x] = TFrame.WithAlpha(target[x], a);
            }
        }
    }

    private static void ClearAlpha<TFrame>(Span<TFrame> pixels) where TFrame : unmanaged, IPixelOps<TFrame>
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = TFrame.WithAlpha(pixels[i], 0);
        }
    }
}
