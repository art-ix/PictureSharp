using System.Runtime.InteropServices;
using PictureSharp.Formats.Pcx;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Pcx;

/// <summary>PCX pixels checked against Pillow, and 32-bit files against ImageMagick when it is installed.</summary>
public class PcxOracleTests
{
    private const string PillowRgba = """
        import sys
        from PIL import Image
        with open(sys.argv[2], "wb") as out:
            out.write(Image.open(sys.argv[1]).convert("RGBA").tobytes())
        """;

    [Fact]
    [Trait("Category", "Oracle")]
    public void EncodedFiles_MatchPillowExceptColoredMono()
    {
        if (!HasPillow())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            AssertSame(directory, Solid(Rgba32.Black, Rgba32.White), new());
            AssertSame(directory, Palette(4), new());
            AssertSame(directory, Palette(8), new() { BitsPerPixel = 4 });
            AssertSame(directory, Palette(30), new() { BitsPerPixel = 8 });
            AssertSame(directory, GrayRamp(), new());
            AssertSame(directory, Rgb(), new() { BitsPerPixel = 24 });

            using Image colored = Palette(2);
            colored[0, 0] = new Rgba32(255, 0, 0);
            colored[1, 0] = new Rgba32(0, 0, 255);
            byte[] pcx = colored.ToPcx(new() { BitsPerPixel = 1 });
            string path = Path.Combine(directory, "colored.pcx");
            File.WriteAllBytes(path, pcx);
            using Image ours = Image.Load(pcx);
            byte[] pillow = PillowRgbaBytes(path);
            Assert.False(pillow.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(ours.GetPixelSpan())),
                "Pillow ignores a 1-bit PCX palette and should not match a colored bilevel image.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void MultirowBitAndRunBoundaries_MatchPillow()
    {
        if (!HasPillow())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (int bits in new[] { 0, 1, 4, 8, 24 })
            {
                foreach (int width in new[] { 1, 3, 7, 8, 9, 15, 16, 17, 62, 63, 64, 65, 126, 127 })
                {
                    var image = new Image(width, 5);
                    for (int y = 0; y < image.Height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int index = x < width / 2 ? y : x + (y * 17);
                            index %= bits switch { 0 => 4, 1 => 2, 4 => 16, _ => 256 };
                            image[x, y] = bits == 1
                                ? index == 0 ? Rgba32.Black : Rgba32.White
                                : new Rgba32((byte)(index * 73), (byte)(index * 151), (byte)(index * 199));
                        }
                    }

                    byte[] pcx = image.ToPcx(new() { BitsPerPixel = bits });
                    using (Image decoded = Image.Load(pcx))
                    {
                        Assert.Equal(image.GetPixelSpan().ToArray(), decoded.GetPixelSpan().ToArray());
                    }

                    // Pillow 10.2 skips RGB plane compaction at widths 1 and 3 (even stride).
                    // Keep the spec-compliant stride and use ImageMagick for these cases.
                    if (bits == 24 && width is 1 or 3)
                    {
                        using (image)
                        {
                            AssertImageMagick(directory, image, pcx);
                        }
                    }
                    else
                    {
                        AssertSame(directory, image, new() { BitsPerPixel = bits });
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void PaddedPlanes_MatchImageMagickWhenInstalled()
    {
        if (ReferenceTools.Run("convert", "-version", out _) != 0)
        {
            return;
        }

        byte[] pcx = new PcxBuilder
        {
            Width = 2,
            Height = 2,
            Bits = 8,
            Planes = 3,
            BytesPerLine = 8,
            Rows = [
                [1, 2, 0, 0, 0, 0, 0, 0, 3, 4, 0, 0, 0, 0, 0, 0, 5, 6, 0, 0, 0, 0, 0, 0],
                [7, 8, 0, 0, 0, 0, 0, 0, 9, 10, 0, 0, 0, 0, 0, 0, 11, 12, 0, 0, 0, 0, 0, 0],
            ],
        }.Build();
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "padded.pcx");
            string raw = Path.Combine(directory, "padded.rgba");
            File.WriteAllBytes(path, pcx);
            Assert.True(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0, error);
            using Image image = Image.Load(pcx);
            Assert.Equal(File.ReadAllBytes(raw), MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void Rgba_MatchesImageMagickWhenInstalled()
    {
        if (ReferenceTools.Run("convert", "-version", out _) != 0)
        {
            return;
        }

        using var image = new Image(3, 2);
        image[0, 0] = new Rgba32(255, 0, 0, 128);
        image[1, 0] = new Rgba32(0, 255, 0, 255);
        image[2, 0] = new Rgba32(0, 0, 255, 0);
        image[0, 1] = new Rgba32(1, 2, 3, 4);
        image[1, 1] = new Rgba32(250, 251, 252, 253);
        image[2, 1] = Rgba32.White;
        byte[] pcx = image.ToPcx(new() { BitsPerPixel = 32 });
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "rgba.pcx");
            string raw = Path.Combine(directory, "rgba.raw");
            File.WriteAllBytes(path, pcx);
            Assert.True(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0, error);
            byte[] expected = MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray();
            Assert.Equal(expected, File.ReadAllBytes(raw));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void AssertImageMagick(string directory, Image image, byte[] pcx)
    {
        if (ReferenceTools.Run("convert", "-version", out _) != 0)
        {
            return;
        }

        string path = Path.Combine(directory, "image.pcx");
        string raw = Path.Combine(directory, "image.rgba");
        File.WriteAllBytes(path, pcx);
        Assert.True(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0, error);
        Assert.Equal(MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray(), File.ReadAllBytes(raw));
    }

    private static void AssertSame(string directory, Image image, PcxEncoderOptions options)
    {
        using (image)
        {
            byte[] pcx = image.ToPcx(options);
            string path = Path.Combine(directory, $"{options.BitsPerPixel}-{image.Width}.pcx");
            File.WriteAllBytes(path, pcx);
            byte[] pillow = PillowRgbaBytes(path);
            Assert.True(pillow.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(image.GetPixelSpan())), path);
        }
    }

    private static Image Solid(params Rgba32[] colors)
    {
        var image = new Image(colors.Length, 1);
        for (int i = 0; i < colors.Length; i++)
        {
            image[i, 0] = colors[i];
        }

        return image;
    }

    private static Image Palette(int colors)
    {
        var image = new Image(colors, 1);
        for (int i = 0; i < colors; i++)
        {
            image[i, 0] = new Rgba32((byte)(i * 20), (byte)(255 - (i * 15)), (byte)(i * 8));
        }

        return image;
    }

    private static Image GrayRamp()
    {
        var image = new Image(20, 1);
        for (int i = 0; i < 20; i++)
        {
            image[i, 0] = new Rgba32((byte)(i * 12), (byte)(i * 12), (byte)(i * 12));
        }

        return image;
    }

    private static Image Rgb()
    {
        var image = new Image(5, 2);
        for (int i = 0; i < 10; i++)
        {
            image.GetPixelSpan()[i] = new Rgba32((byte)(i * 25), (byte)(100 + i), (byte)(i * 13));
        }

        return image;
    }

    private static bool HasPillow() => ReferenceTools.Run("python3", "-I -c \"import PIL\"", out _) == 0;

    private static byte[] PillowRgbaBytes(string path)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "rgba.py");
            string raw = Path.Combine(directory, "out.rgba");
            File.WriteAllText(script, PillowRgba);
            Assert.True(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{raw}\"", out string error) == 0, error);
            return File.ReadAllBytes(raw);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
