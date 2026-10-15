using System.Runtime.InteropServices;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Gif;

/// <summary>GIF files written by PictureSharp and GIF↔WebP conversion checked with libwebp 1.6.0 tools and Pillow.</summary>
public class GifOracleTests
{
    private const string PillowFrames = """
        import sys
        import numpy as np
        from PIL import Image, ImageSequence
        with open(sys.argv[2], "wb") as out:
            for frame in ImageSequence.Iterator(Image.open(sys.argv[1])):
                a = np.array(frame.convert("RGBA"))
                a[a[..., 3] == 0] = 0
                out.write(a.tobytes())
        """;

    private static IEnumerable<(string Name, Image Image)> EncoderInputs()
    {
        foreach (string file in Directory.GetFiles(Path.Combine(Corpus.CorpusDir, "webp", "anim")).Order(StringComparer.Ordinal))
        {
            yield return (Path.GetFileName(file), Image.Load(file));
        }

        foreach (string key in Corpus.References.Keys.Where(k => k.StartsWith("gif/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            yield return (key, Image.Load(Corpus.PathOf(key)));
        }

        yield return ("photo_alpha", Image.Load(Corpus.PathOf("src/photo_alpha.png")));
        yield return ("many_colors", QuantizationTests.ManyColors());
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void EncodedGifs_DecodeIdenticallyInAnimDumpAndPillow()
    {
        if (!ReferenceTools.HasGifTools)
        {
            return;
        }

        bool pillow = ReferenceTools.Run("python3", "-I -c \"import numpy, PIL\"", out _) == 0;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-gif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "frames.py");
            File.WriteAllText(script, PillowFrames);
            foreach ((string name, Image image) in EncoderInputs())
            {
                using (image)
                {
                    foreach (GifEncoderOptions options in new GifEncoderOptions[] { new(), new() { Dither = GifDither.FloydSteinberg, Quantizer = GifQuantizer.Octree, Interlace = true }, new() { OptimizeFrames = false, ColorCount = 16 } })
                    {
                        byte[] gif = image.ToGif(options);
                        string path = Path.Combine(directory, "out.gif");
                        File.WriteAllBytes(path, gif);
                        using Image ours = Image.Load(gif);
                        byte[][] expected = ours.Frames.Select(f => MemoryMarshal.AsBytes(f.GetPixelSpan()).ToArray()).ToArray();

                        List<byte[]>? dumped = ReferenceTools.AnimDump(path);
                        Assert.NotNull(dumped);
                        Assert.Equal(expected.Length, dumped.Count);
                        for (int i = 0; i < expected.Length; i++)
                        {
                            Assert.True(expected[i].AsSpan().SequenceEqual(dumped[i]), $"{name} {options}: anim_dump frame {i} differs");
                        }

                        if (pillow)
                        {
                            string raw = Path.Combine(directory, "pillow.rgba");
                            Assert.True(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{raw}\"", out string error) == 0, error);
                            Assert.True(expected.SelectMany(f => f).SequenceEqual(File.ReadAllBytes(raw)), $"{name} {options}: Pillow frames differ");
                        }
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
    public void Convert_MatchesGif2WebP()
    {
        if (!ReferenceTools.HasGifTools)
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-gif2webp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            int compared = 0;
            foreach (string key in Corpus.References.Keys.Where(k => k.StartsWith("gif/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                if (key == "gif/hand_no_color_table.gif")
                {
                    continue;   // gif2webp leaves frames without a color table transparent; PictureSharp reads them as grayscale like Pillow
                }

                string expectedPath = Path.Combine(directory, "expected.webp"), actualPath = Path.Combine(directory, "actual.webp");
                if (ReferenceTools.Run("gif2webp", $"-quiet \"{Corpus.PathOf(key)}\" -o \"{expectedPath}\"", out _) != 0)
                {
                    continue;   // gif2webp rejects files PictureSharp reads tolerantly
                }

                PictureSharp.Formats.WebP.WebPConverter.Convert(Corpus.PathOf(key), actualPath);
                using Image expected = Image.Load(expectedPath);
                using Image actual = Image.Load(actualPath);
                Assert.Equal(expected.FrameCount, actual.FrameCount);
                Assert.Equal(expected.LoopCount, actual.LoopCount);
                Assert.Equal(expected.AnimationBackgroundColor, actual.AnimationBackgroundColor);
                Assert.Equal(expected.Metadata.Xmp, actual.Metadata.Xmp);
                for (int i = 0; i < expected.FrameCount; i++)
                {
                    Assert.True(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan()), $"{key}: frame {i} differs");
                    Assert.Equal(expected.Frames[i].Duration, actual.Frames[i].Duration);
                }

                Assert.Equal(0, ReferenceTools.Run("webpmux", $"-info \"{actualPath}\"", out string info));
                if (actual.IsAnimated)
                {
                    Assert.Contains($"Number of frames: {actual.FrameCount}", info, StringComparison.Ordinal);
                }

                // The lossless WebP converts back to the same GIF frames.
                using Image gif = Image.Load(Corpus.PathOf(key));
                using Image back = Image.Load(actual.ToGif());
                Assert.Equal(actual.FrameCount, back.FrameCount);
                Assert.Equal(actual.LoopCount, back.LoopCount);
                for (int i = 0; i < back.FrameCount; i++)
                {
                    Assert.True(actual.Frames[i].GetPixelSpan().SequenceEqual(back.Frames[i].GetPixelSpan()), $"{key}: GIF frame {i} differs after WebP");
                    Assert.Equal(actual.Frames[i].Duration, back.Frames[i].Duration);
                }

                compared++;
            }

            Assert.True(compared >= 27, $"only {compared} files compared");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void Gif2WebP_ReadsOurGifs()
    {
        if (!ReferenceTools.HasGifTools)
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-ourgif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.GetFiles(Path.Combine(Corpus.CorpusDir, "webp", "anim")))
            {
                using Image source = Image.Load(file);
                string gifPath = Path.Combine(directory, "ours.gif"), webpPath = Path.Combine(directory, "ours.webp");
                File.WriteAllBytes(gifPath, source.ToGif());
                Assert.True(ReferenceTools.Run("gif2webp", $"-quiet \"{gifPath}\" -o \"{webpPath}\"", out string error) == 0, error);
                using Image gif = Image.Load(gifPath);
                using Image webp = Image.Load(webpPath);
                Assert.Equal(gif.FrameCount, webp.FrameCount);
                Assert.Equal(gif.LoopCount, webp.LoopCount);
                for (int i = 0; i < gif.FrameCount; i++)
                {
                    Assert.True(gif.Frames[i].GetPixelSpan().SequenceEqual(webp.Frames[i].GetPixelSpan()), $"{file}: frame {i} differs");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
