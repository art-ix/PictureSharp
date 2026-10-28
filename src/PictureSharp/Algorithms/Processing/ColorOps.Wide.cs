using System.Numerics;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

internal static partial class ColorOps
{
    /// <summary><see cref="Grayscale(Span{Rgba32})"/>; other depths use the same integer BT.601 luma at their depth.</summary>
    public readonly struct GrayscaleKernel : IPixelKernel
    {
        public void Run(Rgba32[] pixels) => Grayscale(pixels);

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            float scale = 1f / TPixel.MaxValue;
            for (int i = 0; i < pixels.Length; i++)
            {
                TPixel p = pixels[i];
                float l = TPixel.Luma(p) * scale;
                pixels[i] = TPixel.WithAlpha(TPixel.FromUnit(new Vector4(l, l, l, 0f)), TPixel.Alpha(p));
            }
        }
    }

    /// <summary><see cref="Invert(Span{Rgba32})"/>; other depths subtract from their maximum.</summary>
    public readonly struct InvertKernel : IPixelKernel
    {
        public void Run(Rgba32[] pixels) => Invert(pixels);

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba64 p = TPixel.ToRgba64(pixels[i]);
                pixels[i] = TPixel.FromRgba64(new Rgba64((ushort)(ushort.MaxValue - p.R), (ushort)(ushort.MaxValue - p.G), (ushort)(ushort.MaxValue - p.B), p.A));
            }
        }
    }

    /// <summary>
    /// <see cref="Sepia(Span{Rgba32})"/>; other depths apply the same matrix to unrounded values, clamped and rounded
    /// half up once.
    /// </summary>
    public readonly struct SepiaKernel : IPixelKernel
    {
        public void Run(Rgba32[] pixels) => Sepia(pixels);

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Vector4 p = TPixel.ToUnit(pixels[i]);
                float r = (0.393f * p.X) + (0.769f * p.Y) + (0.189f * p.Z);
                float g = (0.349f * p.X) + (0.686f * p.Y) + (0.168f * p.Z);
                float b = (0.272f * p.X) + (0.534f * p.Y) + (0.131f * p.Z);
                pixels[i] = TPixel.WithAlpha(TPixel.FromUnit(new Vector4(r, g, b, 0f)), TPixel.Alpha(pixels[i]));
            }
        }
    }

    /// <summary>
    /// <see cref="MakeTransparent(Span{Rgba32}, Rgba32)"/> with the key stored at the frame's depth (rounded to 8 bits
    /// on an 8-bit frame) and matched exactly.
    /// </summary>
    public readonly struct MakeTransparentKernel(Rgba64 key) : IPixelKernel
    {
        public void Run(Rgba32[] pixels) => MakeTransparent(pixels, key.ToRgba32());

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel stored = TPixel.FromRgba64(key);
            for (int i = 0; i < pixels.Length; i++)
            {
                if (TPixel.MaxChannelDifference(pixels[i], stored) == 0)
                {
                    pixels[i] = TPixel.WithAlpha(pixels[i], 0);
                }
            }
        }
    }
}
