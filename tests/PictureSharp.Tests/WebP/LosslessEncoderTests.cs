using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LosslessEncoderTests
{
    public static TheoryData<string> Sources
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string path in Directory.GetFiles(Corpus.PathOf("src"), "*.png").Order()) data.Add("src/" + Path.GetFileName(path));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Sources))]
    public void Corpus_RoundTripsExactly(string source)
    {
        using Image image = Image.Load(Corpus.PathOf(source));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decoded = Image.Load(encoded.AsMemory());
        Assert.Equal(image.ToPixelData(PixelFormat.Rgba32), decoded.ToPixelData(PixelFormat.Rgba32));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    [Trait("Category", "Oracle")]
    public void Corpus_OpensInDwebp_AndMeetsSizeTarget(string source)
    {
        if (!ReferenceTools.HasDwebp || !ReferenceTools.HasCwebp) return;
        using Image image = Image.Load(Corpus.PathOf(source));
        string output = Path.Combine(Path.GetTempPath(), $"picturesharp-{Guid.NewGuid():N}.webp");
        string reference = output + ".ref.webp";
        try
        {
            byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
            File.WriteAllBytes(output, encoded);
            Assert.Equal(image.ToPixelData(PixelFormat.Rgba32), ReferenceTools.DwebpRgba(output));
            Assert.Equal(0, ReferenceTools.Run("cwebp", $"-quiet -lossless -exact -q 75 -m 4 \"{Corpus.PathOf(source)}\" -o \"{reference}\"", out _));
            long expected = new FileInfo(reference).Length;
            Assert.True(encoded.Length <= expected * 1.01, $"{source}: managed {encoded.Length}, cwebp {expected}, ratio {(double)encoded.Length / expected:F4}");
        }
        finally { File.Delete(output); File.Delete(reference); }
    }

    public static TheoryData<string, string> ByteExactCases()
    {
        var data = new TheoryData<string, string>();
        string[] configs = ["-m 0 -q 75", "-m 2 -q 50", "-m 4 -q 75", "-m 4 -q 100", "-m 5 -q 90", "-m 6 -q 100", "-m 4 -q 75 -near_lossless 60", "-m 4 -q 75 -exact"];
        foreach (string source in Directory.GetFiles(Corpus.PathOf("src"), "*.png").Order())
            foreach (string config in configs) data.Add("src/" + Path.GetFileName(source), config);
        return data;
    }

    [Theory]
    [MemberData(nameof(ByteExactCases))]
    [Trait("Category", "Oracle")]
    public void Corpus_MatchesCwebpBitstream(string source, string cwebpArguments)
    {
        if (!ReferenceTools.HasCwebp) return;
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
            Assert.Equal(0, ReferenceTools.Run("cwebp", $"-quiet -lossless {cwebpArguments} \"{Corpus.PathOf(source)}\" -o \"{reference}\"", out _));
            Assert.Equal(Vp8LChunk(File.ReadAllBytes(reference)), Vp8LChunk(image.ToWebP(options)));
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
