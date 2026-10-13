using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.WebP;

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
        using Image image = Image.FromFile(Corpus.PathOf(source));
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
        using Image decoded = Image.FromBytes(encoded.AsMemory());
        Assert.Equal(image.ToPixelArray(PixelFormat.Rgba32), decoded.ToPixelArray(PixelFormat.Rgba32));
    }

    [Theory]
    [MemberData(nameof(Sources))]
    [Trait("Category", "Oracle")]
    public void Corpus_OpensInDwebp_AndMeetsSizeTarget(string source)
    {
        if (!ReferenceTools.HasDwebp || !ReferenceTools.HasCwebp) return;
        using Image image = Image.FromFile(Corpus.PathOf(source));
        string output = Path.Combine(Path.GetTempPath(), $"rastersharp-{Guid.NewGuid():N}.webp");
        string reference = output + ".ref.webp";
        try
        {
            byte[] encoded = image.ToWebP(new WebPEncoderOptions { Lossless = true, Exact = true });
            File.WriteAllBytes(output, encoded);
            Assert.Equal(image.ToPixelArray(PixelFormat.Rgba32), ReferenceTools.DwebpRgba(output));
            Assert.Equal(0, ReferenceTools.Run("cwebp", $"-quiet -lossless -exact -q 75 -m 4 \"{Corpus.PathOf(source)}\" -o \"{reference}\"", out _));
            long expected = new FileInfo(reference).Length;
            Assert.True(encoded.Length <= expected * 1.01, $"{source}: managed {encoded.Length}, cwebp {expected}, ratio {(double)encoded.Length / expected:F4}");
        }
        finally { File.Delete(output); File.Delete(reference); }
    }
}
