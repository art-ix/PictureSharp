using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<PictureSharp.Adapters.Tests.ProcessorCountLimit>]

namespace PictureSharp.Adapters.Tests;

/// <summary>One running test per core, as in PictureSharp.Tests.</summary>
public sealed class ProcessorCountLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}

internal static class TestImages
{
    /// <summary>Odd widths exercise row padding; the first column holds alpha 0, 255 and intermediate values.</summary>
    public static IEnumerable<(int Width, int Height)> Sizes() => [(1, 1), (3, 2), (7, 5), (13, 4)];

    /// <summary>A deterministic image with every alpha class: 0 (with nonzero color), 255 and intermediate values.</summary>
    public static Image Create(int width, int height, int seed = 1)
    {
        var image = new Image(width, height);
        var random = new Random(seed);
        byte[] alphas = [0, 255, 1, 128, 254, 37];
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = image.GetRowSpan<Rgba32>(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), alphas[((y * width) + x) % alphas.Length]);
            }
        }

        return image;
    }

    /// <summary>
    /// A deterministic 16-bit image: samples that are not multiples of 257, every alpha class (0 with nonzero color,
    /// 65535, low and intermediate values).
    /// </summary>
    public static Image CreateWide(int width, int height, int seed = 1)
    {
        var image = new Image(width, height, PixelType.Rgba64);
        var random = new Random(seed);
        ushort[] alphas = [0, 65535, 1, 7, 32768, 65534, 1234];
        for (int y = 0; y < height; y++)
        {
            Span<Rgba64> row = image.GetRowSpan<Rgba64>(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba64((ushort)random.Next(65536), (ushort)random.Next(65536), (ushort)random.Next(65536), alphas[((y * width) + x) % alphas.Length]);
            }
        }

        return image;
    }

    public static Rgba64[] WidePixels(Image image) => image.GetPixelSpan<Rgba64>().ToArray();

    /// <summary>The 8-bit image PictureSharp itself produces from a 16-bit one (one rounding per sample).</summary>
    public static Rgba32[] Rounded(Image image) => [.. image.GetPixelSpan<Rgba64>().ToArray().Select(p => p.ToRgba32())];

    /// <summary>The same image with every pixel opaque.</summary>
    public static Image CreateOpaque(int width, int height, int seed = 1)
    {
        Image image = Create(width, height, seed);
        foreach (ref Rgba32 pixel in image.GetPixelSpan<Rgba32>()) pixel.A = 255;
        return image;
    }

    /// <summary>Straight RGBA after a premultiplied round trip through PictureSharp's own conversion.</summary>
    public static Rgba32[] ThroughPremultiplied(Image image)
    {
        using Image back = Image.LoadPixelData(image.ToPixelData(PixelFormat.Rgba32Premultiplied), image.Width, image.Height, PixelFormat.Rgba32Premultiplied);
        return back.GetPixelSpan<Rgba32>().ToArray();
    }

    public static Rgba32[] Pixels(Image image) => image.GetPixelSpan<Rgba32>().ToArray();
}
