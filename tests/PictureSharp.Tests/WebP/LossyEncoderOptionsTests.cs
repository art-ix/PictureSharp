using System.Buffers.Binary;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyEncoderOptionsTests
{
    [Fact]
    [Trait("Category", "Oracle")]
    public void TuningOptions_MatchCwebp160()
    {
        if (!ReferenceTools.HasCwebp) return;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-lossy-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string expectedPath = Path.Combine(directory, "expected.webp"), actualPath = Path.Combine(directory, "actual.webp");
            foreach ((WebPEncoderOptions options, string flags) in Cases())
                foreach (string name in new[] { "photo.png", "odd_alpha.png" })
                {
                    string source = Corpus.PathOf($"src/{name}");
                    using var image = Image.Load(source);
                    byte[] actual = image.ToWebP(options);
                    Assert.True(ReferenceTools.Run("cwebp", $"-quiet {flags} \"{source}\" -o \"{expectedPath}\"", out string output) == 0, output);
                    byte[] expected = File.ReadAllBytes(expectedPath);
                    File.WriteAllBytes(actualPath, actual);
                    byte[] actualPixels = ReferenceTools.DwebpRgba(actualPath)!;
                    Assert.NotNull(actualPixels);
                    byte[] expectedPixels = ReferenceTools.DwebpRgba(expectedPath)!;
                    bool equal = expected.AsSpan().SequenceEqual(actual);
                    if (!equal)
                    {
                        bool samePixels = expectedPixels.AsSpan().SequenceEqual(actualPixels);
                        if (!samePixels || actual.Length > 1.01 * expected.Length)
                        {
                            File.WriteAllBytes("/tmp/picturesharp-options-expected.webp", expected);
                            File.WriteAllBytes("/tmp/picturesharp-options-actual.webp", actual);
                        }
                        Assert.True(samePixels && actual.Length <= 1.01 * expected.Length, $"{name}: {flags}, expected={expected.Length}, actual={actual.Length}, samePixels={samePixels}");
                    }
                    using var decoded = Image.Load(actual.AsMemory());
                    Assert.Equal(actualPixels, decoded.ToPixelData(PixelFormat.Rgba32));
                }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [Trait("Category", "Oracle")]
    public void TokenPartitions_MatchNativeApi()
    {
        string root = Path.GetFullPath(Path.Combine(Corpus.Assets, "..", ".."));
        string reference = Path.Combine(root, "reference", "libwebp-1.6.0"), build = Path.Combine(root, "reference", "libwebp-1.6.0-build");
        if (!File.Exists(Path.Combine(build, "libwebp.a"))) return;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-partitions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string oracle = Path.Combine(directory, "oracle"), raw = Path.Combine(directory, "input.rgba"), expected = Path.Combine(directory, "expected.webp");
            string source = Path.Combine(Corpus.Assets, "vp8_encode_oracle.c");
            Assert.True(ReferenceTools.Run("cc", $"-I\"{reference}\" \"{source}\" \"{build}/libwebp.a\" \"{build}/libsharpyuv.a\" -lm -lpthread -o \"{oracle}\"", out string error) == 0, error);
            using var image = new Image(65, 49);
            var random = new Random(7523);
            foreach (ref Rgba32 pixel in image.GetPixelSpan()) pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            File.WriteAllBytes(raw, image.ToPixelData(PixelFormat.Rgba32));
            foreach (int method in new[] { 0, 1, 2, 4, 6 })
                foreach (int parts in Enumerable.Range(0, 4))
                {
                    byte[] actual = image.ToWebP(new WebPEncoderOptions { Method = method, Partitions = parts });
                    Assert.True(ReferenceTools.Run(oracle, $"\"{raw}\" \"{expected}\" 65 49 {method} {parts}", out string output) == 0, output);
                    Assert.True(File.ReadAllBytes(expected).AsSpan().SequenceEqual(actual), $"method={method}, parts={parts}");
                }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void DefaultsAndFacade_ProduceLossyWebP()
    {
        using var image = new Image(17, 19, System.Drawing.Color.CornflowerBlue);
        byte[] encoded = image.ToWebP();
        Assert.True(encoded.AsSpan(12, 4).SequenceEqual("VP8 "u8));
        Assert.Equal(encoded, image.Encode(ImageFormat.WebP));
        using var output = new MemoryStream(); image.Save(output, ImageFormat.WebP);
        Assert.True(output.CanWrite); Assert.Equal(encoded, output.ToArray());
        using var decoded = Image.Load(encoded.AsMemory());
        Assert.Equal(image.Width, decoded.Width); Assert.Equal(image.Height, decoded.Height); Assert.True(decoded.IsOpaque());
    }

    [Fact]
    public void Encoding_PreservesSourcePixels_AndLosslessAlpha()
    {
        using var image = new Image(17, 19);
        var random = new Random(7261);
        foreach (ref Rgba32 p in image.GetPixelSpan()) p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        Rgba32[] before = image.GetPixelSpan().ToArray();
        foreach (int method in new[] { 0, 4, 6 })
            foreach (bool exact in new[] { false, true })
            {
                byte[] encoded = image.ToWebP(new WebPEncoderOptions { Method = method, Exact = exact });
                using var decoded = Image.Load(encoded.AsMemory());
                Assert.Equal(before, image.GetPixelSpan().ToArray());
                Assert.Equal(before.Select(p => p.A), decoded.GetPixelSpan().ToArray().Select(p => p.A));
            }
    }

    [Fact]
    public void InvalidOptionsAndDimensions_FailBeforeWriting()
    {
        using var image = new Image(1, 1);
        using var output = new MemoryStream();
        // Options are checked when set, before anything is encoded.
        foreach (Func<WebPEncoderOptions> create in new Func<WebPEncoderOptions>[]
        {
            () => new WebPEncoderOptions { TargetPsnr = float.NaN },
            () => new WebPEncoderOptions { TargetPsnr = float.PositiveInfinity },
            () => new WebPEncoderOptions { Preset = (WebPPreset)6 },
            () => new WebPEncoderOptions { AlphaFilter = (WebPAlphaFilter)3 },
            () => new WebPEncoderOptions { MaxDegreeOfParallelism = 0 },
        }) Assert.Throws<ArgumentOutOfRangeException>(() => image.Save(output, create()));
        using var wide = new Image(16384, 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => wide.Save(output, new WebPEncoderOptions()));
        Assert.Equal(0, output.Length);
    }

    private static IEnumerable<(WebPEncoderOptions Options, string Flags)> Cases()
    {
        foreach (WebPPreset preset in Enum.GetValues<WebPPreset>())
            yield return (new() { Preset = preset }, $"-preset {preset.ToString().ToLowerInvariant()} -q 75 -m 4");
        foreach (int segments in Enumerable.Range(1, 4))
            yield return (new() { Segments = segments }, $"-q 75 -m 4 -segments {segments}");
        foreach (int strength in new[] { 0, 100 })
            yield return (new() { SnsStrength = strength }, $"-q 75 -m 4 -sns {strength}");
        foreach (int strength in new[] { 0, 100 })
            foreach (int sharpness in new[] { 0, 7 })
                yield return (new() { FilterStrength = strength, FilterSharpness = sharpness }, $"-q 75 -m 4 -f {strength} -sharpness {sharpness}");
        yield return (new() { AutoFilter = true }, "-q 75 -m 4 -af");
        yield return (new() { UseSharpYuv = true }, "-q 75 -m 4 -sharp_yuv");
        yield return (new() { Exact = true }, "-q 75 -m 4 -exact");
        foreach (int method in new[] { 0, 4, 6 })
        {
            yield return (new() { Method = method, Passes = 3 }, $"-q 75 -m {method} -pass 3");
            yield return (new() { Method = method, Passes = 6, TargetSize = 1000 }, $"-q 75 -m {method} -pass 6 -size 1000");
            yield return (new() { Method = method, Passes = 6, TargetPsnr = 36 }, $"-q 75 -m {method} -pass 6 -psnr 36");
        }
        foreach (WebPAlphaFilter filter in Enum.GetValues<WebPAlphaFilter>())
            foreach (bool compression in new[] { false, true })
                foreach (int quality in new[] { 0, 45, 70, 90, 100 })
                    yield return (new() { AlphaFilter = filter, AlphaCompression = compression, AlphaQuality = quality }, $"-q 75 -m 4 -alpha_filter {filter.ToString().ToLowerInvariant()} -alpha_method {(compression ? 1 : 0)} -alpha_q {quality}");
    }

    [Theory]
    [InlineData(MetadataKinds.None)]
    [InlineData(MetadataKinds.All)]
    [InlineData(MetadataKinds.Exif)]
    [InlineData(MetadataKinds.Icc | MetadataKinds.Xmp)]
    public void MetadataAndAlpha_UseOrderedPaddedChunks(MetadataKinds selection)
    {
        using var image = new Image(3, 5);
        image.GetPixelSpan().Fill(new Rgba32(50, 120, 200, 100));
        image.Metadata.IccProfile = [1, 2, 3]; image.Metadata.Exif = [4, 5, 6, 7, 8]; image.Metadata.Xmp = [9, 10, 11];
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Metadata = selection });
        Assert.Equal(encoded.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4)));
        using var decoded = Image.Load(encoded.AsMemory());
        Assert.Equal(selection.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        Assert.Equal(selection.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        Assert.Equal(selection.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
        Assert.All(decoded.GetPixelSpan().ToArray(), p => Assert.Equal(100, p.A));
    }
}
