using System.Runtime.InteropServices;
using PictureSharp.Domain.Hdr;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// ISO 21496-1 reconstruction against libultrahdr v2.0.2 (scripts/build-ultrahdr-reference.sh). The math is compared on
/// identical decoded pixels through tests/assets/ultrahdr_oracle.cpp, separately from libultrahdr's own JPEG decoding
/// and YUV conversion, which the full-pipeline comparison with ultrahdr_app includes. Skipped without the tools.
/// </summary>
[Property("Category", "Oracle")]
public class HdrUltraHdrOracleTests
{
    private static System.Text.Json.JsonElement Budget => HdrGainMapTests.Manifest.GetProperty("oracle");

    public static IEnumerable<string> IsoCases()
    {
        HashSet<string> excluded = [.. Budget.GetProperty("excluded").EnumerateArray().Select(e => e.GetString()!)];
        return HdrGainMapTests.ValidCases().Where(n => n.StartsWith("iso_", StringComparison.Ordinal) && !excluded.Contains(n));
    }

    internal readonly record struct Stats(double MaxExcess, double MedianRelative, double P99Relative, double MaxAbsolute);

    /// <summary>Per-channel errors of <paramref name="actual"/> against <paramref name="expected"/> (RGB triplets).</summary>
    internal static Stats Measure(ReadOnlySpan<Rgba128> actual, ReadOnlySpan<float> expected, double absolute, double relative, int stride = 3,
        Func<float, float>? clamp = null)
    {
        var rel = new double[actual.Length * 3];
        double maxExcess = double.NegativeInfinity, maxAbs = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            ReadOnlySpan<float> a = [actual[i].R, actual[i].G, actual[i].B];
            for (int c = 0; c < 3; c++)
            {
                double v = clamp is null ? a[c] : clamp(a[c]);
                double e = expected[(i * stride) + c];
                double d = Math.Abs(v - e);
                maxAbs = Math.Max(maxAbs, d);
                maxExcess = Math.Max(maxExcess, d - (absolute + (relative * Math.Abs(e))));
                rel[(i * 3) + c] = d / Math.Max(Math.Abs(e), 1e-2);
            }
        }

        Array.Sort(rel);
        return new(maxExcess, rel[rel.Length / 2], rel[(int)(rel.Length * 0.99)], maxAbs);
    }

    /// <summary>Runs the harness on the image's own decoded base pixels and gain map; returns (exact, LUT) RGB floats.</summary>
    private static (float[] Exact, float[] Lut) Oracle(Image image, string name)
    {
        GainMapAttachment attachment = image.GainMap!;
        GainMapParameters p = attachment.Parameters!;
        DecodedGainMap map = attachment.Decoder!.Decode(attachment.MapData, 1, 1, default);
        string input = Path.Combine(Path.GetTempPath(), $"uhdr-{name}-{Guid.NewGuid():N}.bin"), output = input + ".f32";
        try
        {
            using (var writer = new BinaryWriter(File.Create(input)))
            {
                foreach (int v in (int[])[image.Width, image.Height, map.Width, map.Height, p.ChannelCount]) writer.Write(v);
                writer.Write(1f);
                foreach (float v in p.GainMapMin) writer.Write(float.Exp2(v));
                foreach (float v in p.GainMapMax) writer.Write(float.Exp2(v));
                foreach (float v in p.Gamma) writer.Write(v);
                foreach (float v in p.BaseOffset) writer.Write(v);
                foreach (float v in p.AlternateOffset) writer.Write(v);
                writer.Write(float.Exp2(p.BaseHeadroom));
                writer.Write(float.Exp2(p.AlternateHeadroom));
                foreach (Rgba32 px in image.GetPixelSpan()) { writer.Write(px.R); writer.Write(px.G); writer.Write(px.B); }
                foreach (Rgba32 px in map.Pixels)
                {
                    writer.Write(px.R);
                    if (p.ChannelCount == 3) { writer.Write(px.G); writer.Write(px.B); }
                }
            }

            int code = ReferenceTools.Run("ultrahdr_oracle", $"\"{input}\" \"{output}\"", out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_oracle failed ({code}): {log}");
            float[] all = MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(output)).ToArray();
            return (all[..(all.Length / 2)], all[(all.Length / 2)..]);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task Math_MatchesLibUltraHdr_OnIdenticalPixels(string name)
    {
        Skip.Unless(ReferenceTools.HasUltraHdr, "libultrahdr v2.0.2 tools are not built (scripts/build-ultrahdr-reference.sh).");
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
        using HdrImage hdr = image.ToHdr();
        (float[] exact, float[] lut) = Oracle(image, name);
        System.Text.Json.JsonElement tolerance = HdrGainMapTests.Manifest.GetProperty("tolerance");
        Stats e = Measure(hdr.GetPixelSpan(), exact, tolerance.GetProperty("absolute").GetDouble(), tolerance.GetProperty("relative").GetDouble());
        Stats l = Measure(hdr.GetPixelSpan(), lut, 0, 0);
        await Assert.That(e.MaxExcess).IsLessThanOrEqualTo(0).Because($"{name}: exact functions {e}");
        await Assert.That(l.MaxAbsolute).IsLessThanOrEqualTo(Budget.GetProperty("lut_max_absolute").GetDouble()).Because($"{name}: LUT functions {l}");
    }

    [Test]
    [Property("Category", "Local")]
    [MethodDataSource(typeof(HdrSamplesTests), nameof(HdrSamplesTests.Samples))]
    public async Task IphoneMath_MatchesLibUltraHdr_OnIdenticalPixels(string path)
    {
        Skip.Unless(ReferenceTools.HasUltraHdr, "libultrahdr v2.0.2 tools are not built (scripts/build-ultrahdr-reference.sh).");
        using Image image = Image.Load(path, new DecoderOptions { LoadGainMap = true });
        using HdrImage hdr = image.ToHdr();
        (float[] exact, _) = Oracle(image, Path.GetFileNameWithoutExtension(path));
        // Apple's Display P3 profile stores the sRGB curve as quantized s15Fixed16 parameters; PictureSharp linearizes
        // with the profile's curve, libultrahdr with the exact sRGB formula (relative differences up to ~1e-4).
        Stats e = Measure(hdr.GetPixelSpan(), exact, 1e-5, 1e-4);
        await Assert.That(e.MaxExcess).IsLessThanOrEqualTo(0).Because($"{path}: exact functions {e}");
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task FullPipeline_MatchesUltraHdrApp(string name)
    {
        Skip.Unless(ReferenceTools.HasUltraHdr, "libultrahdr v2.0.2 tools are not built (scripts/build-ultrahdr-reference.sh).");
        string path = HdrGainMapTests.File(name + ".jpg"), output = Path.Combine(Path.GetTempPath(), $"uhdr-{name}-{Guid.NewGuid():N}.raw");
        try
        {
            int code = ReferenceTools.Run("ultrahdr_app", $"-m 1 -j \"{path}\" -o 0 -O 4 -z \"{output}\"", out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_app failed ({code}): {log}");
            float[] reference = [.. MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(output)).ToArray().Select(h => (float)h)];
            using Image image = Image.Load(path, new DecoderOptions { LoadGainMap = true });
            using HdrImage hdr = image.ToHdr();
            Stats s = Measure(hdr.GetPixelSpan(), reference, 0, 0, stride: 4, clamp: v => Math.Clamp(v, 0f, 10000f / 203f));
            await Assert.That(s.MaxAbsolute).IsLessThanOrEqualTo(Budget.GetProperty("pipeline_max_absolute").GetDouble()).Because($"{name}: {s}");
            await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(Budget.GetProperty("pipeline_median_relative").GetDouble()).Because($"{name}: {s}");
            await Assert.That(s.P99Relative).IsLessThanOrEqualTo(Budget.GetProperty("pipeline_p99_relative").GetDouble()).Because($"{name}: {s}");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
