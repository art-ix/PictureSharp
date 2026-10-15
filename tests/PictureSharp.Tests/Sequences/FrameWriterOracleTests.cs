using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Sequences;

public class FrameWriterOracleTests
{
    public static IEnumerable<string> Formats() => FrameWriterTests.Formats();

    [Test]
    [Property("Category", "Oracle")]
    [MethodDataSource(nameof(Formats))]
    public async Task SequentialFiles_MatchIndependentFramesTimingAndLoop(string format)
    {
        Skip.Unless(ReferenceTools.HasPillow, "Pillow is not available.");
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-sequence-oracle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "animation." + format);
            using Image source = FrameWriterTests.Pattern(23);
            await using (var writer = Image.CreateFrameWriter(path, source.Size, FrameWriterTests.Options(format), new() { LoopCount = 3 }))
            {
                foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
                await writer.CompleteAsync();
            }
            string script = Path.Combine(directory, "frames.py"), raw = Path.Combine(directory, "frames.rgba"), info = Path.Combine(directory, "info.txt");
            File.WriteAllText(script, """
                import sys
                from PIL import Image, ImageSequence
                image = Image.open(sys.argv[1])
                durations = []
                with open(sys.argv[2], "wb") as out:
                    for frame in ImageSequence.Iterator(image):
                        # Pillow 10.2 appends the interlace decoder argument on every APNG frame.
                        if frame.format == "PNG":
                            frame.decoderconfig = ()
                        out.write(frame.convert("RGBA").tobytes())
                        durations.append(round(frame.info.get("duration", 0)))
                with open(sys.argv[3], "w") as out:
                    out.write(str(image.info.get("loop", -1)) + "\n" + ",".join(map(str, durations)))
                """);
            await Assert.That(ReferenceTools.Run("python3", $"-I \"{script}\" \"{path}\" \"{raw}\" \"{info}\"", out string error)).IsEqualTo(0).Because(error);
            await Assert.That(File.ReadAllBytes(raw).SequenceEqual(source.Frames.SelectMany(f => f.GetPixelByteSpan().ToArray()))).IsTrue();
            string[] details = File.ReadAllLines(info);
            await Assert.That(details[0]).IsEqualTo(format == "gif" ? "2" : "3");
            await Assert.That(details[1]).IsEqualTo(string.Join(',', source.Frames.Select(f => (int)f.Duration.TotalMilliseconds)));
            if (format == "webp" && ReferenceTools.HasGifTools)
            {
                List<byte[]>? frames = ReferenceTools.AnimDump(path);
                Assert.NotNull(frames);
                await Assert.That(frames.Count).IsEqualTo(source.FrameCount);
                for (int i = 0; i < frames.Count; i++)
                    await Assert.That(frames[i].AsSpan().SequenceEqual(source.Frames[i].GetPixelByteSpan())).IsTrue();
                await Assert.That(ReferenceTools.Run("webpmux", $"-info \"{path}\"", out string output)).IsEqualTo(0);
                await Assert.That(output).Contains("Number of frames: 23", StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
