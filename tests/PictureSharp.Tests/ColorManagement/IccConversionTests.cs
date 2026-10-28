using System.Text.Json;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.ColorManagement;

/// <summary>ICC matrix/TRC conversions against LittleCMS references (tests/assets/color_reference.py).</summary>
public class IccConversionTests
{
    internal static JsonElement Manifest { get; } = JsonDocument.Parse(File.ReadAllText(Corpus.PathOf("color/color_reference.json"))).RootElement;

    public static IEnumerable<string> Profiles() => Manifest.GetProperty("profiles").EnumerateObject().Select(p => p.Name);

    public static IEnumerable<string> InvalidProfiles() => Manifest.GetProperty("invalid").EnumerateObject().Select(p => p.Name);

    internal static string Color(string name) => Corpus.PathOf($"color/{name}");

    /// <summary>Asserts the plan's tolerance: per-channel difference and the share of differing channels.</summary>
    internal static async Task AssertWithinTolerance(Image actual, Image expected, bool gray, string because)
    {
        JsonElement tolerance = Manifest.GetProperty("tolerance");
        int maxAbs = tolerance.GetProperty(gray ? "gray_max_abs" : "rgb_max_abs").GetInt32();
        double maxFraction = tolerance.GetProperty("max_mismatch_fraction").GetDouble();
        await Assert.That(actual.Size).IsEqualTo(expected.Size);
        (int worst, double fraction) = Difference(actual.Frames.RootFrame.GetPixelSpan<Rgba32>(), expected.Frames.RootFrame.GetPixelSpan<Rgba32>());
        await Assert.That(worst).IsLessThanOrEqualTo(maxAbs).Because(because);
        await Assert.That(fraction).IsLessThanOrEqualTo(maxFraction).Because(because);
    }

    /// <summary>The largest per-channel difference (alpha must match exactly) and the share of differing RGB channels.</summary>
    internal static (int Worst, double Fraction) Difference(ReadOnlySpan<Rgba32> a, ReadOnlySpan<Rgba32> e)
    {
        int worst = 0, mismatched = 0;
        for (int i = 0; i < a.Length; i++)
        {
            foreach (int d in (ReadOnlySpan<int>)[a[i].R - e[i].R, a[i].G - e[i].G, a[i].B - e[i].B])
            {
                worst = Math.Max(worst, Math.Abs(d));
                if (d != 0) mismatched++;
            }

            if (a[i].A != e[i].A) worst = int.MaxValue;
        }

        return (worst, (double)mismatched / (a.Length * 3));
    }

    [Test]
    [MethodDataSource(nameof(Profiles))]
    public async Task MatrixTrcConversion_MatchesLittleCms(string name)
    {
        JsonElement entry = Manifest.GetProperty("profiles").GetProperty(name);
        IccColorModel model = IccProfileParser.Parse(File.ReadAllBytes(Color(entry.GetProperty("profile").GetString()!)));
        using Image image = Image.Load(Color(entry.GetProperty("input").GetString()!));
        new SrgbConversion(model).Apply(image.Frames.RootFrame.GetPixelSpan<Rgba32>());
        using Image expected = Image.Load(Color(entry.GetProperty("reference").GetString()!));
        await AssertWithinTolerance(image, expected, model.IsGray, name);
    }

    [Test]
    [MethodDataSource(nameof(InvalidProfiles))]
    public async Task InvalidProfiles_ThrowTheDocumentedException(string name)
    {
        JsonElement entry = Manifest.GetProperty("invalid").GetProperty(name);
        byte[] data = File.ReadAllBytes(Color(entry.GetProperty("file").GetString()!));
        Exception? error = null;
        try { IccProfileParser.Parse(data); }
        catch (Exception ex) { error = ex; }
        await Assert.That(error?.GetType().Name).IsEqualTo(entry.GetProperty("expected").GetString());
    }

    [Test]
    public async Task DeclaredSizeBeyondData_IsClampedLikeLittleCms()
    {
        byte[] data = File.ReadAllBytes(Color("display_p3_v4_para3.icc"));
        byte[] oversized = (byte[])data.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(oversized, (uint)data.Length + 50);
        Rgba32[] a = [new(200, 30, 90), new(5, 250, 128)], b = [.. a];
        new SrgbConversion(IccProfileParser.Parse(data)).Apply(a);
        new SrgbConversion(IccProfileParser.Parse(oversized)).Apply(b);
        await Assert.That(b).IsEquivalentTo(a, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Conversion_KeepsAlpha()
    {
        IccColorModel model = IccProfileParser.Parse(File.ReadAllBytes(Color("display_p3_v4_para3.icc")));
        Rgba32[] pixels = [new(255, 0, 0, 0), new(10, 200, 30, 77), new(0, 0, 255, 255)];
        new SrgbConversion(model).Apply(pixels);
        await Assert.That(pixels.Select(p => p.A)).IsEquivalentTo([(byte)0, (byte)77, (byte)255], CollectionOrdering.Matching);
        await Assert.That(pixels[0].R).IsEqualTo((byte)255); // P3 red is outside sRGB and clips.
    }

    [Test]
    public async Task SrgbTransfer_RoundTripsEveryValue()
    {
        for (int i = 0; i < 256; i++)
        {
            await Assert.That(SrgbTransfer.Encode(SrgbTransfer.Decode((byte)i))).IsEqualTo((byte)i);
        }

        await Assert.That(SrgbTransfer.Encode(float.NaN)).IsEqualTo((byte)0);
        await Assert.That(SrgbTransfer.Encode(-1f)).IsEqualTo((byte)0);
        await Assert.That(SrgbTransfer.Encode(2f)).IsEqualTo((byte)255);
    }

    [Test]
    public async Task Parser_FuzzedProfiles_ThrowOnlyDocumentedExceptions()
    {
        byte[] good = File.ReadAllBytes(Color("display_p3_v4_para3.icc"));
        var random = new Random(62);
        int parsed = 0;
        for (int i = 0; i < 2000; i++)
        {
            byte[] data = (byte[])good.Clone();
            int edits = random.Next(1, 8);
            for (int e = 0; e < edits; e++) data[random.Next(data.Length)] = (byte)random.Next(256);
            if (random.Next(4) == 0) data = data[..random.Next(data.Length)];
            try
            {
                IccColorModel model = IccProfileParser.Parse(data);
                new SrgbConversion(model).Apply(new Rgba32[] { new(1, 2, 3), new(255, 128, 0) });
                parsed++;
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException) { }
        }

        await Assert.That(parsed).IsGreaterThan(0); // Some edits only touch values, not the structure.
    }
}
