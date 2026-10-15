using System.Text.Json;
using PictureSharp.Algorithms.Quantization;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Gif;

/// <summary>Color quantization against Pillow 10.2.0 (tests/assets/gif_reference.py).</summary>
public class QuantizationTests
{
    internal sealed record Reference(int[] Palette, string Sha256, string RgbaSha256);

    internal static readonly Lazy<Dictionary<string, Reference>> References = new(() =>
        JsonSerializer.Deserialize<Dictionary<string, Reference>>(
            File.ReadAllText(Path.Combine(Corpus.Assets, "gif_quantize_reference.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static TheoryData<string> Keys()
    {
        var data = new TheoryData<string>();
        foreach (string key in References.Value.Keys)
        {
            data.Add(key);
        }

        return data;
    }

    /// <summary>The source as Pillow's convert("RGB") sees it: alpha dropped.</summary>
    internal static uint[] LoadOpaque(string name, out Rgba32[] pixels, out int width, out int height)
    {
        using Image image = name == "many_colors" ? ManyColors() : ImageReader.Load(Path.Combine(Corpus.CorpusDir, "src", name + ".png"));
        (width, height) = (image.Width, image.Height);
        pixels = image.GetPixelSpan().ToArray();
        var packed = new uint[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i].A = 255;
            packed[i] = (uint)(pixels[i].R | (pixels[i].G << 8) | (pixels[i].B << 16) | (255 << 24));
        }

        return packed;
    }

    /// <summary>More than 65536 distinct colors, so median cut rescales its pixel hash (same formula in gif_reference.py).</summary>
    internal static Image ManyColors()
    {
        var image = new Image(512, 300);
        for (int y = 0; y < 300; y++)
        {
            for (int x = 0; x < 512; x++)
            {
                image[x, y] = new Rgba32((byte)x, (byte)y, (byte)(((x >> 8) * 128) + (x * y)));
            }
        }

        return image;
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void Quantize_MatchesPillow(string key)
    {
        string[] parts = key.Split('/');
        int colors = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        uint[] packed = LoadOpaque(parts[0], out Rgba32[] pixels, out int width, out int height);
        var indices = new byte[packed.Length];
        Rgba32[] palette;
        switch (parts[1])
        {
            case "mediancut":
                palette = MedianCutQuantizer.Quantize(packed, colors, indices);
                break;
            case "octree":
                palette = OctreeQuantizer.Quantize(packed, colors, indices);
                break;
            default:
                palette = MedianCutQuantizer.Quantize(packed, colors, indices);
                new PaletteMapper(palette).Map(pixels, width, height, dither: true, new bool[pixels.Length], 0, indices);
                break;
        }

        Reference expected = References.Value[key];
        int[] actualPalette = palette.SelectMany(c => new int[] { c.R, c.G, c.B }).ToArray();
        Assert.Equal(expected.Palette, actualPalette);
        Assert.Equal(expected.Sha256, Corpus.Sha256(indices));
    }
}
