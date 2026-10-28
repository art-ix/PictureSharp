using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>Per-pixel color operations on frame buffers.</summary>
internal static class ColorOps
{
    /// <summary>Replaces the color with its BT.601 luma (Pillow's "L" conversion); alpha is kept.</summary>
    public static void Grayscale(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            byte l = PixelConverter.Luma(p.R, p.G, p.B);
            pixels[i] = new Rgba32(l, l, l, p.A);
        }
    }

    /// <summary>Inverts the color channels; alpha is kept.</summary>
    public static void Invert(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            pixels[i] = new Rgba32((byte)(255 - p.R), (byte)(255 - p.G), (byte)(255 - p.B), p.A);
        }
    }

    /// <summary>
    /// Overwrites <paramref name="rect"/> (already clipped to the frame) of every frame with a color, rounded once to
    /// the storage depth.
    /// </summary>
    public readonly struct FillFrame(ImageFrame frame, Rectangle rect, Rgba64 color) : IPixelOperation<bool>
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] pixels = frame.Buffer.As<TPixel>();
            TPixel value = TPixel.FromRgba64(color);
            if (rect.Width == frame.Width)
            {
                pixels.AsSpan(rect.Top * frame.Width, rect.Height * frame.Width).Fill(value);
                return true;
            }

            for (int y = rect.Top; y < rect.Bottom; y++)
            {
                pixels.AsSpan((y * frame.Width) + rect.X, rect.Width).Fill(value);
            }

            return true;
        }
    }

    /// <summary>Sets alpha to 0 on pixels equal to <paramref name="key"/>, keeping their color.</summary>
    public static void MakeTransparent(Span<Rgba32> pixels, Rgba32 key)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] == key)
            {
                pixels[i].A = 0;
            }
        }
    }

    /// <summary>
    /// Composites the frame over a background. An 8-bit frame over an 8-bit color uses <see cref="Compositor.Over"/>;
    /// otherwise the blend runs on unrounded values and is rounded once to the storage depth.
    /// </summary>
    public readonly struct BackgroundFrame(ImageFrame frame, Rgba64 background) : IPixelOperation<bool>
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] pixels = frame.Buffer.As<TPixel>();
            if (typeof(TPixel) == typeof(Rgba32) && PixelDepth.IsExact8Bit([background]))
            {
                BackgroundColor(Unsafe.As<Rgba32[]>(pixels), background.ToRgba32());
                return true;
            }

            Vector4 backdrop = EncodedUnit.Load(background);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = TPixel.FromUnit(Compositor.OverWide(backdrop, TPixel.ToUnit(pixels[i])));
            }

            return true;
        }
    }

    /// <summary>Composites the pixels over <paramref name="background"/>.</summary>
    public static void BackgroundColor(Span<Rgba32> pixels, Rgba32 background)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = Compositor.Over(background, pixels[i]);
        }
    }

    /// <summary>
    /// Scales alpha by an opacity: 8-bit frames with the rounding of <see cref="Compositor.DrawOver"/>, others rounding
    /// the exact product half up.
    /// </summary>
    public readonly struct OpacityFrame(ImageFrame frame, float opacity) : IPixelOperation<bool>
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            TPixel[] pixels = frame.Buffer.As<TPixel>();
            if (typeof(TPixel) == typeof(Rgba32))
            {
                Opacity(Unsafe.As<Rgba32[]>(pixels), opacity);
                return true;
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = TPixel.WithAlpha(pixels[i], (int)((TPixel.Alpha(pixels[i]) * (double)opacity) + 0.5));
            }

            return true;
        }
    }

    /// <summary>Scales alpha by <paramref name="opacity"/> with the rounding of <see cref="Compositor.DrawOver"/>.</summary>
    public static void Opacity(Span<Rgba32> pixels, float opacity)
    {
        Span<byte> alphaMap = stackalloc byte[256];
        Compositor.FillAlphaMap(alphaMap, opacity);
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = alphaMap[pixels[i].A];
        }
    }

    /// <summary>Applies the classic sepia matrix in fixed point (thousandths), rounding half up and clamping.</summary>
    public static void Sepia(Span<Rgba32> pixels)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            int r = ((393 * p.R) + (769 * p.G) + (189 * p.B) + 500) / 1000;
            int g = ((349 * p.R) + (686 * p.G) + (168 * p.B) + 500) / 1000;
            int b = ((272 * p.R) + (534 * p.G) + (131 * p.B) + 500) / 1000;
            pixels[i] = new Rgba32((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255), p.A);
        }
    }
}
