using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyEncoderTests
{
    [Test]
    [Property("Category", "Oracle")]
    public async Task CorpusEncoding_MatchesCwebp160()
    {
        Skip.Unless(ReferenceTools.HasCwebp, "cwebp 1.6.0 is not available.");
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-lossy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string expectedPath = Path.Combine(directory, "expected.webp"), actualPath = Path.Combine(directory, "actual.webp");
            var failures = new List<string>();
            foreach (string source in Directory.GetFiles(Corpus.PathOf("src"), "*.png"))
                foreach (int quality in new[] { 0, 35, 75, 100 })
                    foreach (int method in Enumerable.Range(0, 7))
                    {
                        using var image = Image.Load(source);
                        var options = new WebPEncoderOptions { Quality = quality, Method = method, Metadata = MetadataKinds.None };
                        byte[] actual = image.ToWebP(options);
                        await Assert.That(ReferenceTools.Run("cwebp", $"-quiet -q {quality} -m {method} \"{source}\" -o \"{expectedPath}\"", out string output) == 0).IsTrue().Because(output);
                        byte[] expected = File.ReadAllBytes(expectedPath);
                        File.WriteAllBytes(actualPath, actual);
                        Assert.NotNull(ReferenceTools.DwebpRgba(actualPath));
                        if (!expected.AsSpan().SequenceEqual(actual))
                        {
                            int mismatch = Enumerable.Range(0, Math.Min(expected.Length, actual.Length)).FirstOrDefault(i => expected[i] != actual[i], -1);
                            byte[] expectedPixels = ReferenceTools.DwebpRgba(expectedPath)!;
                            byte[] actualPixels = ReferenceTools.DwebpRgba(actualPath)!;
                            if (actual.Length <= expected.Length * 1.01 && expectedPixels.AsSpan().SequenceEqual(actualPixels)) continue;
                            failures.Add($"{Path.GetFileName(source)}, q={quality}, m={method}, expected={expected.Length}, actual={actual.Length}, mismatch={mismatch}, samePixels={expectedPixels.AsSpan().SequenceEqual(actualPixels)}");
                            if (failures.Count == 1)
                            {
                                File.WriteAllBytes("/tmp/picturesharp-lossy-expected.webp", expected);
                                File.WriteAllBytes("/tmp/picturesharp-lossy-actual.webp", actual);
                            }
                        }
                    }
            await Assert.That(failures.Count == 0).IsTrue().Because(string.Join("\n", failures));
        }
        finally { Directory.Delete(directory, true); }
    }
}
