using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LosslessEncoderTests
{
    public static TestCases<string> Sources
    {
        get
        {
            var data = new TestCases<string>();
            foreach (string path in Directory.GetFiles(Corpus.PathOf("src"), "*.png").Order()) data.Add("src/" + Path.GetFileName(path));
            return data;
        }
    }

    [Test]
    [MethodDataSource(nameof(Sources))]
    public async Task Corpus_RoundTripsExactly(string source)
    {
        using Image image = Image.Load(Corpus.PathOf(source));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decoded = Image.Load(encoded.AsMemory());
        await Assert.That(decoded.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo(image.ToPixelData(PixelFormat.Rgba32), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(Sources))]
    [Property("Category", "Oracle")]
    public async Task Corpus_OpensInDwebp_AndMeetsSizeTarget(string source)
    {
        Skip.Unless(ReferenceTools.HasDwebp && ReferenceTools.HasCwebp, "cwebp 1.6.0 and dwebp are not available.");
        using Image image = Image.Load(Corpus.PathOf(source));
        string output = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.webp");
        string reference = output + ".ref.webp";
        try
        {
            byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
            File.WriteAllBytes(output, encoded);
            await Assert.That(ReferenceTools.DwebpRgba(output)).IsEquivalentTo(image.ToPixelData(PixelFormat.Rgba32), CollectionOrdering.Matching);
            await Assert.That(ReferenceTools.Run("cwebp", $"-quiet -lossless -exact -q 75 -m 4 \"{Corpus.PathOf(source)}\" -o \"{reference}\"", out _)).IsEqualTo(0);
            long expected = new FileInfo(reference).Length;
            await Assert.That(encoded.Length <= expected * 1.01).IsTrue().Because($"{source}: managed {encoded.Length}, cwebp {expected}, ratio {(double)encoded.Length / expected:F4}");
        }
        finally { File.Delete(output); File.Delete(reference); }
    }

    public static TestCases<string, string> ByteExactCases()
    {
        var data = new TestCases<string, string>();
        string[] configs = ["-m 0 -q 75", "-m 2 -q 50", "-m 4 -q 75", "-m 4 -q 100", "-m 5 -q 90", "-m 6 -q 100", "-m 4 -q 75 -near_lossless 60", "-m 4 -q 75 -exact"];
        foreach (string source in Directory.GetFiles(Corpus.PathOf("src"), "*.png").Order())
            foreach (string config in configs) data.Add("src/" + Path.GetFileName(source), config);
        return data;
    }

    [Test]
    [MethodDataSource(nameof(ByteExactCases))]
    [Property("Category", "Oracle")]
    public async Task Corpus_MatchesCwebpBitstream(string source, string cwebpArguments)
    {
        Skip.Unless(ReferenceTools.HasCwebp, "cwebp 1.6.0 is not available.");
        string[] args = cwebpArguments.Split(' ');
        int Value(string name, int fallback) => Array.IndexOf(args, name) is int i and >= 0 ? int.Parse(args[i + 1]) : fallback;
        var options = new WebPEncoderOptions
        {
            Lossless = true,
            Method = Value("-m", 4),
            Quality = Value("-q", 75),
            NearLossless = Value("-near_lossless", 100),
            Exact = args.Contains("-exact"),
        };
        using Image image = Image.Load(Corpus.PathOf(source));
        string reference = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.webp");
        try
        {
            await Assert.That(ReferenceTools.Run("cwebp", $"-quiet -lossless {cwebpArguments} \"{Corpus.PathOf(source)}\" -o \"{reference}\"", out _)).IsEqualTo(0);
            await Assert.That(Vp8LChunk(image.ToWebP(options))).IsEquivalentTo(Vp8LChunk(File.ReadAllBytes(reference)), CollectionOrdering.Matching);
        }
        finally { File.Delete(reference); }
    }

    private static byte[] Vp8LChunk(byte[] webp)
    {
        for (int offset = 12; offset + 8 <= webp.Length;)
        {
            int size = BitConverter.ToInt32(webp, offset + 4);
            if (webp.AsSpan(offset, 4).SequenceEqual("VP8L"u8)) return webp.AsSpan(offset + 8, size).ToArray();
            offset += 8 + size + (size & 1);
        }

        throw new InvalidDataException("No VP8L chunk.");
    }
}
