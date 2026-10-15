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

    [Test]
    [Property("Category", "Oracle")]
    public async Task EncodedGifs_DecodeIdenticallyInAnimDumpAndPillow()
    {
        Skip.Unless(ReferenceTools.HasGifTools, "gif2webp and anim_dump 1.6.0 are not available.");

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
                        await Assert.That(dumped.Count).IsEqualTo(expected.Length);
                        for (int i = 0; i < expected.Length; i++)
                        {
                            await Assert.That(expected[i].AsSpan().SequenceEqual(dumped[i])).IsTrue().Because($"{name} {options}: anim_dump frame {i} differs");
                        }

                        if (pillow)
                        {
                            string raw = Path.Combine(directory, "pillow.rgba");
                            await Assert.That(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{raw}\"", out string error) == 0).IsTrue().Because(error);
                            await Assert.That(expected.SelectMany(f => f).SequenceEqual(File.ReadAllBytes(raw))).IsTrue().Because($"{name} {options}: Pillow frames differ");
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

    [Test]
    [Property("Category", "Oracle")]
    public async Task Convert_MatchesGif2WebP()
    {
        Skip.Unless(ReferenceTools.HasGifTools, "gif2webp and anim_dump 1.6.0 are not available.");

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
                await Assert.That(actual.FrameCount).IsEqualTo(expected.FrameCount);
                await Assert.That(actual.LoopCount).IsEqualTo(expected.LoopCount);
                await Assert.That(actual.AnimationBackgroundColor).IsEqualTo(expected.AnimationBackgroundColor);
                await Sequence.AssertEqual(expected.Metadata.Xmp, actual.Metadata.Xmp);
                for (int i = 0; i < expected.FrameCount; i++)
                {
                    await Assert.That(expected.Frames[i].GetPixelSpan().SequenceEqual(actual.Frames[i].GetPixelSpan())).IsTrue().Because($"{key}: frame {i} differs");
                    await Assert.That(actual.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
                }

                await Assert.That(ReferenceTools.Run("webpmux", $"-info \"{actualPath}\"", out string info)).IsEqualTo(0);
                if (actual.IsAnimated)
                {
                    await Assert.That(info).Contains($"Number of frames: {actual.FrameCount}", StringComparison.Ordinal);
                }

                // The lossless WebP converts back to the same GIF frames.
                using Image gif = Image.Load(Corpus.PathOf(key));
                using Image back = Image.Load(actual.ToGif());
                await Assert.That(back.FrameCount).IsEqualTo(actual.FrameCount);
                await Assert.That(back.LoopCount).IsEqualTo(actual.LoopCount);
                for (int i = 0; i < back.FrameCount; i++)
                {
                    await Assert.That(actual.Frames[i].GetPixelSpan().SequenceEqual(back.Frames[i].GetPixelSpan())).IsTrue().Because($"{key}: GIF frame {i} differs after WebP");
                    await Assert.That(back.Frames[i].Duration).IsEqualTo(actual.Frames[i].Duration);
                }

                compared++;
            }

            await Assert.That(compared >= 27).IsTrue().Because($"only {compared} files compared");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task Gif2WebP_ReadsOurGifs()
    {
        Skip.Unless(ReferenceTools.HasGifTools, "gif2webp and anim_dump 1.6.0 are not available.");

        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-ourgif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (string file in Directory.GetFiles(Path.Combine(Corpus.CorpusDir, "webp", "anim")))
            {
                using Image source = Image.Load(file);
                string gifPath = Path.Combine(directory, "ours.gif"), webpPath = Path.Combine(directory, "ours.webp");
                File.WriteAllBytes(gifPath, source.ToGif());
                await Assert.That(ReferenceTools.Run("gif2webp", $"-quiet \"{gifPath}\" -o \"{webpPath}\"", out string error) == 0).IsTrue().Because(error);
                using Image gif = Image.Load(gifPath);
                using Image webp = Image.Load(webpPath);
                await Assert.That(webp.FrameCount).IsEqualTo(gif.FrameCount);
                await Assert.That(webp.LoopCount).IsEqualTo(gif.LoopCount);
                for (int i = 0; i < gif.FrameCount; i++)
                {
                    await Assert.That(gif.Frames[i].GetPixelSpan().SequenceEqual(webp.Frames[i].GetPixelSpan())).IsTrue().Because($"{file}: frame {i} differs");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
