using System.Buffers.Binary;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyEncoderOptionsTests
{
    [Test]
    [Property("Category", "Oracle")]
    public async Task TuningOptions_MatchCwebp160()
    {
        Skip.Unless(ReferenceTools.HasCwebp, "cwebp 1.6.0 is not available.");
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
                    await Assert.That(ReferenceTools.Run("cwebp", $"-quiet {flags} \"{source}\" -o \"{expectedPath}\"", out string output) == 0).IsTrue().Because(output);
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
                        await Assert.That(samePixels && actual.Length <= 1.01 * expected.Length).IsTrue().Because($"{name}: {flags}, expected={expected.Length}, actual={actual.Length}, samePixels={samePixels}");
                    }
                    using var decoded = Image.Load(actual.AsMemory());
                    await Assert.That(decoded.ToPixelData(PixelFormat.Rgba32)).IsEquivalentTo(actualPixels, CollectionOrdering.Matching);
                }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    [Property("Category", "Oracle")]
    public async Task TokenPartitions_MatchNativeApi()
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
            await Assert.That(ReferenceTools.Run("cc", $"-I\"{reference}\" \"{source}\" \"{build}/libwebp.a\" \"{build}/libsharpyuv.a\" -lm -lpthread -o \"{oracle}\"", out string error) == 0).IsTrue().Because(error);
            using var image = new Image(65, 49);
            var random = new Random(7523);
            foreach (ref Rgba32 pixel in image.GetPixelSpan<Rgba32>()) pixel = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            File.WriteAllBytes(raw, image.ToPixelData(PixelFormat.Rgba32));
            foreach (int method in new[] { 0, 1, 2, 4, 6 })
                foreach (int parts in Enumerable.Range(0, 4))
                {
                    byte[] actual = image.ToWebP(new WebPEncoderOptions { Method = method, Partitions = parts });
                    await Assert.That(ReferenceTools.Run(oracle, $"\"{raw}\" \"{expected}\" 65 49 {method} {parts}", out string output) == 0).IsTrue().Because(output);
                    await Assert.That(File.ReadAllBytes(expected).AsSpan().SequenceEqual(actual)).IsTrue().Because($"method={method}, parts={parts}");
                }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    public async Task DefaultsAndFacade_ProduceLossyWebP()
    {
        using var image = new Image(17, 19, System.Drawing.Color.CornflowerBlue);
        byte[] encoded = image.ToWebP();
        await Assert.That(encoded.AsSpan(12, 4).SequenceEqual("VP8 "u8)).IsTrue();
        await Assert.That(image.Encode(ImageFormat.WebP)).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        using var output = new MemoryStream(); image.Save(output, ImageFormat.WebP);
        await Assert.That(output.CanWrite).IsTrue(); await Assert.That(output.ToArray()).IsEquivalentTo(encoded, CollectionOrdering.Matching);
        using var decoded = Image.Load(encoded.AsMemory());
        await Assert.That(decoded.Width).IsEqualTo(image.Width); await Assert.That(decoded.Height).IsEqualTo(image.Height); await Assert.That(decoded.IsOpaque()).IsTrue();
    }

    [Test]
    public async Task Encoding_PreservesSourcePixels_AndLosslessAlpha()
    {
        using var image = new Image(17, 19);
        var random = new Random(7261);
        foreach (ref Rgba32 p in image.GetPixelSpan<Rgba32>()) p = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        Rgba32[] before = image.GetPixelSpan<Rgba32>().ToArray();
        foreach (int method in new[] { 0, 4, 6 })
            foreach (bool exact in new[] { false, true })
            {
                byte[] encoded = image.ToWebP(new WebPEncoderOptions { Method = method, Exact = exact });
                using var decoded = Image.Load(encoded.AsMemory());
                await Assert.That(image.GetPixelSpan<Rgba32>().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
                await Assert.That(decoded.GetPixelSpan<Rgba32>().ToArray().Select(p => p.A)).IsEquivalentTo(before.Select(p => p.A), CollectionOrdering.Matching);
            }
    }

    [Test]
    public async Task InvalidOptionsAndDimensions_FailBeforeWriting()
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
        }) Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => image.Save(output, create()));
        using var wide = new Image(16384, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => wide.Save(output, new WebPEncoderOptions()));
        await Assert.That(output.Length).IsEqualTo(0);
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

    [Test]
    [Arguments(MetadataKinds.None)]
    [Arguments(MetadataKinds.All)]
    [Arguments(MetadataKinds.Exif)]
    [Arguments(MetadataKinds.Icc | MetadataKinds.Xmp)]
    public async Task MetadataAndAlpha_UseOrderedPaddedChunks(MetadataKinds selection)
    {
        using var image = new Image(3, 5);
        image.GetPixelSpan<Rgba32>().Fill(new Rgba32(50, 120, 200, 100));
        image.Metadata.IccProfile = [1, 2, 3]; image.Metadata.Exif = [4, 5, 6, 7, 8]; image.Metadata.Xmp = [9, 10, 11];
        byte[] encoded = image.ToWebP(new WebPEncoderOptions { Metadata = selection });
        await Assert.That((int)BinaryPrimitives.ReadUInt32LittleEndian(encoded.AsSpan(4))).IsEqualTo(encoded.Length - 8);
        using var decoded = Image.Load(encoded.AsMemory());
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Icc) ? image.Metadata.IccProfile : null, decoded.Metadata.IccProfile);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Exif) ? image.Metadata.Exif : null, decoded.Metadata.Exif);
        await Sequence.AssertEqual(selection.HasFlag(MetadataKinds.Xmp) ? image.Metadata.Xmp : null, decoded.Metadata.Xmp);
        foreach (var p in decoded.GetPixelSpan<Rgba32>().ToArray())
        {
            await Assert.That((int)p.A).IsEqualTo(100);
        }
    }
}
