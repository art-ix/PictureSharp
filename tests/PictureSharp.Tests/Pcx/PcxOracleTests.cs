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

    [Test]
    [Property("Category", "Oracle")]
    public async Task EncodedFiles_MatchPillowExceptColoredMono()
    {
        Skip.Unless(ReferenceTools.HasPillow, "Pillow is not installed.");

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await AssertSame(directory, Solid(Rgba32.Black, Rgba32.White), new());
            await AssertSame(directory, Palette(4), new());
            await AssertSame(directory, Palette(8), new() { BitsPerPixel = 4 });
            await AssertSame(directory, Palette(30), new() { BitsPerPixel = 8 });
            await AssertSame(directory, GrayRamp(), new());
            await AssertSame(directory, Rgb(), new() { BitsPerPixel = 24 });

            using Image colored = Palette(2);
            colored[0, 0] = new Rgba32(255, 0, 0);
            colored[1, 0] = new Rgba32(0, 0, 255);
            byte[] pcx = colored.ToPcx(new() { BitsPerPixel = 1 });
            string path = Path.Combine(directory, "colored.pcx");
            File.WriteAllBytes(path, pcx);
            using Image ours = Image.Load(pcx);
            byte[] pillow = await PillowRgbaBytes(path);
            await Assert.That(pillow.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(ours.GetPixelSpan()))).IsFalse().Because("Pillow ignores a 1-bit PCX palette and should not match a colored bilevel image.");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task MultirowBitAndRunBoundaries_MatchPillow()
    {
        Skip.Unless(ReferenceTools.HasPillow, "Pillow is not installed.");

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
                        await Assert.That(decoded.GetPixelSpan().ToArray()).IsEquivalentTo(image.GetPixelSpan().ToArray(), CollectionOrdering.Matching);
                    }

                    // Pillow 10.2 skips RGB plane compaction at widths 1 and 3 (even stride).
                    // Keep the spec-compliant stride and use ImageMagick for these cases.
                    if (bits == 24 && width is 1 or 3)
                    {
                        using (image)
                        {
                            await AssertImageMagick(directory, image, pcx);
                        }
                    }
                    else
                    {
                        await AssertSame(directory, image, new() { BitsPerPixel = bits });
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task PaddedPlanes_MatchImageMagickWhenInstalled()
    {
        Skip.Unless(ReferenceTools.HasImageMagick, "ImageMagick is not installed.");

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
            await Assert.That(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0).IsTrue().Because(error);
            using Image image = Image.Load(pcx);
            await Assert.That(MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray()).IsEquivalentTo(File.ReadAllBytes(raw), CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task Rgba_MatchesImageMagickWhenInstalled()
    {
        Skip.Unless(ReferenceTools.HasImageMagick, "ImageMagick is not installed.");

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
            await Assert.That(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0).IsTrue().Because(error);
            byte[] expected = MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray();
            await Assert.That(File.ReadAllBytes(raw)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task AssertImageMagick(string directory, Image image, byte[] pcx)
    {
        Skip.Unless(ReferenceTools.HasImageMagick, "ImageMagick is not installed.");

        string path = Path.Combine(directory, "image.pcx");
        string raw = Path.Combine(directory, "image.rgba");
        File.WriteAllBytes(path, pcx);
        await Assert.That(ReferenceTools.Run("convert", $"\"{path}\" -depth 8 \"rgba:{raw}\"", out string error) == 0).IsTrue().Because(error);
        await Assert.That(File.ReadAllBytes(raw)).IsEquivalentTo(MemoryMarshal.AsBytes(image.GetPixelSpan()).ToArray(), CollectionOrdering.Matching);
    }

    private static async Task AssertSame(string directory, Image image, PcxEncoderOptions options)
    {
        using (image)
        {
            byte[] pcx = image.ToPcx(options);
            string path = Path.Combine(directory, $"{options.BitsPerPixel}-{image.Width}.pcx");
            File.WriteAllBytes(path, pcx);
            byte[] pillow = await PillowRgbaBytes(path);
            await Assert.That(pillow.AsSpan().SequenceEqual(MemoryMarshal.AsBytes(image.GetPixelSpan()))).IsTrue().Because(path);
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

    private static async Task<byte[]> PillowRgbaBytes(string path)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-pcx-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "rgba.py");
            string raw = Path.Combine(directory, "out.rgba");
            File.WriteAllText(script, PillowRgba);
            await Assert.That(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{raw}\"", out string error) == 0).IsTrue().Because(error);
            return File.ReadAllBytes(raw);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
