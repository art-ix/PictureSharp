using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Enhance
{
    // BT.601 weights of PixelConverter.Luma, as fractions.
    private const float LumaR601 = 19595f / 65536, LumaG601 = 38470f / 65536, LumaB601 = 7471f / 65536;

    /// <summary>
    /// Brightness, contrast or saturation: the Pillow port on 8-bit pixels; at other depths the same blend
    /// <c>degenerate + factor · (pixel − degenerate)</c> on unrounded values, clamped and rounded half up once (Pillow
    /// truncates), with an unrounded degenerate (mean luma, luma).
    /// </summary>
    public readonly struct Kernel(EnhanceMode mode, float factor) : IPixelKernel
    {
        public void Run(Rgba32[] pixels)
        {
            switch (mode)
            {
                case EnhanceMode.Brightness:
                    Brightness(pixels, factor);
                    break;
                case EnhanceMode.Contrast:
                    Contrast(pixels, factor);
                    break;
                default:
                    Saturate(pixels, factor);
                    break;
            }
        }

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            switch (mode)
            {
                case EnhanceMode.Brightness:
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Vector4 p = TPixel.ToUnit(pixels[i]);
                        pixels[i] = Store<TPixel>(p, Vector3.Zero, factor);
                    }

                    break;
                case EnhanceMode.Contrast:
                    double sum = 0;
                    foreach (TPixel p in pixels)
                    {
                        sum += TPixel.Luma(p);
                    }

                    var mean = new Vector3((float)(sum / pixels.Length / TPixel.MaxValue));
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = Store<TPixel>(TPixel.ToUnit(pixels[i]), mean, factor);
                    }

                    break;
                default:
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        Vector4 p = TPixel.ToUnit(pixels[i]);
                        pixels[i] = Store<TPixel>(p, new Vector3((LumaR601 * p.X) + (LumaG601 * p.Y) + (LumaB601 * p.Z)), factor);
                    }

                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TPixel Store<TPixel>(Vector4 p, Vector3 degenerate, float factor) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            Vector3 color = degenerate + (factor * (new Vector3(p.X, p.Y, p.Z) - degenerate));
            return TPixel.FromUnit(new Vector4(color, p.W));
        }
    }
}

/// <summary>The operation of <see cref="Enhance.Kernel"/>.</summary>
internal enum EnhanceMode
{
    Brightness,
    Contrast,
    Saturate,
}
