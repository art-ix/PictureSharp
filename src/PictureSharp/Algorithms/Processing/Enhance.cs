using System.Runtime.CompilerServices;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Processing;

/// <summary>
/// Brightness, contrast and saturation as Pillow's ImageEnhance: a blend between the image and a "degenerate"
/// version of it (black, mean gray or grayscale) that keeps the alpha channel.
/// </summary>
internal static class Enhance
{
    // port: Pillow src/PIL/ImageEnhance.py Brightness (degenerate: black with the image's alpha)
    public static void Brightness(Span<Rgba32> pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            pixels[i] = Blend(new Rgba32(0, 0, 0, p.A), p, factor);
        }
    }

    // port: Pillow src/PIL/ImageEnhance.py Contrast (degenerate: mean luma, rounded, with the image's alpha)
    public static void Contrast(Span<Rgba32> pixels, float factor)
    {
        long sum = 0;
        foreach (Rgba32 p in pixels)
        {
            sum += PixelConverter.Luma(p.R, p.G, p.B);
        }

        byte mean = (byte)(int)(((double)sum / pixels.Length) + 0.5);
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            pixels[i] = Blend(new Rgba32(mean, mean, mean, p.A), p, factor);
        }
    }

    // port: Pillow src/PIL/ImageEnhance.py Color (degenerate: the image converted to LA and back)
    public static void Saturate(Span<Rgba32> pixels, float factor)
    {
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba32 p = pixels[i];
            byte l = PixelConverter.Luma(p.R, p.G, p.B);
            pixels[i] = Blend(new Rgba32(l, l, l, p.A), p, factor);
        }
    }

    // port: Pillow src/libImaging/Blend.c ImagingBlend (per band, alpha as float)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Rgba32 Blend(Rgba32 in1, Rgba32 in2, float alpha) => alpha is >= 0f and <= 1f
        ? new Rgba32(Interpolate(in1.R, in2.R, alpha), Interpolate(in1.G, in2.G, alpha), Interpolate(in1.B, in2.B, alpha), Interpolate(in1.A, in2.A, alpha))
        : new Rgba32(Extrapolate(in1.R, in2.R, alpha), Extrapolate(in1.G, in2.G, alpha), Extrapolate(in1.B, in2.B, alpha), Extrapolate(in1.A, in2.A, alpha));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Interpolate(int a, int b, float alpha) => (byte)(int)(a + (alpha * (b - a)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte Extrapolate(int a, int b, float alpha)
    {
        float temp = a + (alpha * (b - a));
        return temp <= 0f ? (byte)0 : temp >= 255f ? (byte)255 : (byte)(int)temp;
    }
}
