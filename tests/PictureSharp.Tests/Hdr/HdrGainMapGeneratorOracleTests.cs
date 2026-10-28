using System.Globalization;
using System.Runtime.InteropServices;
using PictureSharp.Formats;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// <c>hdr.ToGainMapImage()</c> against libultrahdr v2.0.2: map codes and ISO 21496-1 payload against
/// <c>UltraHdr::generateGainMap</c> on identical inputs (tests/assets/ultrahdr_generate_oracle.cpp), written files
/// decoded by ultrahdr_app, and the round-trip error against libultrahdr's own encoder, all within the "generator"
/// budget of tests/assets/hdr_reference.json. Skipped without the tools (scripts/build-ultrahdr-reference.sh).
/// </summary>
[Property("Category", "Oracle")]
public class HdrGainMapGeneratorOracleTests
{
    private const string MissingGenerator = "ultrahdr_generate_oracle is not built (scripts/build-ultrahdr-reference.sh).";
    private const string MissingApp = "libultrahdr v2.0.2 tools are not built (scripts/build-ultrahdr-reference.sh).";
    private static readonly DecoderOptions Hdr = new() { LoadGainMap = true };

    /// <summary>
    /// Inputs (synthetic, or reconstructed from a corpus file) × options. Gamma ≠ 1 only without gains below the clamped
    /// minimum: there libultrahdr computes pow() of a negative number (sign flip or NaN), PictureSharp gives code 0.
    /// </summary>
    public static IEnumerable<(string Source, HdrColorSpace Space, int Scale, int Channels, float Gamma)> GeneratorCases()
    {
        (int, int, float)[] options = [(1, 3, 1f), (1, 1, 1f), (2, 3, 1f), (4, 1, 1f), (3, 3, 1f)];
        foreach (string source in new[] { "synthetic", "iso_rgb_p3", "iso_gamma_p3", "apple_makernote" })
        {
            foreach (HdrColorSpace space in HdrGainMapGeneratorTests.Spaces())
            {
                foreach ((int scale, int channels, float gamma) in options)
                {
                    yield return (source, space, scale, channels, gamma);
                }
            }
        }

        yield return ("synthetic", HdrColorSpace.LinearSrgb, 1, 3, 2f);
        yield return ("synthetic", HdrColorSpace.LinearSrgb, 2, 1, 0.5f);
    }

    // The HDR input rounded to half floats, as libultrahdr receives it.
    private static HdrImage Input(string source, HdrColorSpace space)
    {
        using HdrImage raw = source == "synthetic"
            ? HdrSynthetic.Smooth(space)
            : Reconstruct(source, space);
        Rgba128[] pixels = raw.GetPixelSpan().ToArray();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba128((float)(Half)pixels[i].R, (float)(Half)pixels[i].G, (float)(Half)pixels[i].B);
        }

        return new HdrImage(raw.Width, raw.Height, pixels, space, raw.Headroom);
    }

    private static HdrImage Reconstruct(string name, HdrColorSpace space)
    {
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), Hdr);
        return image.ToHdr(new HdrOptions { ColorSpace = space });
    }

    private static byte[] HalfPixels(HdrImage hdr)
    {
        var data = new Half[hdr.Width * hdr.Height * 4];
        ReadOnlySpan<Rgba128> pixels = hdr.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            (data[i * 4], data[(i * 4) + 1], data[(i * 4) + 2], data[(i * 4) + 3]) = ((Half)pixels[i].R, (Half)pixels[i].G, (Half)pixels[i].B, (Half)1f);
        }

        return MemoryMarshal.AsBytes(data.AsSpan()).ToArray();
    }

    [Test]
    [MethodDataSource(nameof(GeneratorCases))]
    public async Task GeneratedMap_MatchesLibUltraHdr(string source, HdrColorSpace space, int scale, int channels, float gamma)
    {
        Skip.Unless(ReferenceTools.HasUltraHdrGenerateOracle, MissingGenerator);
        using HdrImage hdr = Input(source, space);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Scale = scale, ChannelCount = channels, Gamma = gamma });
        EncodableGainMap gainMap = image.GetGainMap(require: true)!;
        using Image map = gainMap.Map.Decode(-1, default);

        string input = Path.Combine(Path.GetTempPath(), $"uhdr-gen-{Guid.NewGuid():N}.bin"), output = input + ".out";
        byte[] expected;
        try
        {
            using (var writer = new BinaryWriter(File.Create(input)))
            {
                foreach (int v in new[] { hdr.Width, hdr.Height, scale, channels, 0, space == HdrColorSpace.LinearSrgb ? 0 : 1 })
                {
                    writer.Write(v);
                }

                writer.Write(gamma);
                writer.Write(hdr.Headroom);
                writer.Write(image.ToPixelData());
                writer.Write(HalfPixels(hdr));
            }

            int code = ReferenceTools.Run("ultrahdr_generate_oracle", $"\"{input}\" \"{output}\"", out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_generate_oracle failed ({code}): {log}");
            expected = File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }

        var reader = new BinaryReader(new MemoryStream(expected));
        (int width, int height, int count) = (reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        await Assert.That((width, height, count)).IsEqualTo((map.Width, map.Height, channels));
        byte[] codes = reader.ReadBytes(width * height * channels);
        bool useBase = reader.ReadByte() != 0;
        reader.ReadBytes(24);   // linear boosts, covered by the payload
        byte[] iso = reader.ReadBytes(reader.ReadInt32());

        ReadOnlySpan<Rgba32> mine = map.GetPixelSpan<Rgba32>();
        long identical = 0;
        int worst = 0;
        for (int i = 0; i < width * height; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int d = Math.Abs((c == 0 ? mine[i].R : c == 1 ? mine[i].G : mine[i].B) - codes[(i * channels) + c]);
                worst = Math.Max(worst, d);
                identical += d == 0 ? 1 : 0;
            }
        }

        System.Text.Json.JsonElement budget = HdrGainMapGeneratorTests.Budget;
        await Assert.That(worst).IsLessThanOrEqualTo(budget.GetProperty("oracle_max_code_difference").GetInt32());
        await Assert.That(identical / (double)codes.Length).IsGreaterThanOrEqualTo(budget.GetProperty("oracle_identical_fraction").GetDouble());
        await Assert.That(image.AttachedGainMap!.Parameters!.UseBaseColorSpace).IsEqualTo(useBase);
        await Assert.That(gainMap.Metadata.ToIso()).IsEquivalentTo(iso, CollectionOrdering.Matching);
    }

    public static IEnumerable<(HdrColorSpace Space, int Scale, int Channels)> FileCases() =>
        HdrGainMapGeneratorTests.Spaces().SelectMany(s => new[] { (s, 1, 3), (s, 2, 1), (s, 4, 3) });

    /// <summary>Generated files decode in ultrahdr_app within the full-pipeline budget against PictureSharp's ToHdr.</summary>
    [Test]
    [MethodDataSource(nameof(FileCases))]
    public async Task GeneratedFiles_DecodeInUltraHdrApp(HdrColorSpace space, int scale, int channels)
    {
        Skip.Unless(ReferenceTools.HasUltraHdr, MissingApp);
        using HdrImage hdr = HdrSynthetic.Smooth(space);
        using Image image = hdr.ToGainMapImage(new GainMapOptions { Scale = scale, ChannelCount = channels });
        byte[] file = image.ToJpeg(new JpegEncoderOptions { Quality = 95, Subsampling = JpegSubsampling.Ratio444, GainMap = JpegGainMapMode.Require });
        string path = Path.Combine(Path.GetTempPath(), $"ps-uhdr-gen-{Guid.NewGuid():N}.jpg"), output = path + ".raw";
        try
        {
            File.WriteAllBytes(path, file);
            int code = ReferenceTools.Run("ultrahdr_app", $"-m 1 -j \"{path}\" -o 0 -O 4 -z \"{output}\"", out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_app failed ({code}): {log}");
            float[] reference = [.. MemoryMarshal.Cast<byte, Half>(File.ReadAllBytes(output)).ToArray().Select(h => (float)h)];
            using Image written = Image.Load(file, Hdr);
            // libultrahdr outputs the gamut of the map's ICC profile (the HDR image's), or the base's without one.
            using HdrImage back = written.ToHdr(new HdrOptions { ColorSpace = space });
            HdrUltraHdrOracleTests.Stats s = HdrUltraHdrOracleTests.Measure(back.GetPixelSpan(), reference, 0, 0, stride: 4, clamp: v => Math.Clamp(v, 0f, 10000f / 203f));
            System.Text.Json.JsonElement budget = HdrGainMapTests.Manifest.GetProperty("oracle");
            await Assert.That(s.MaxAbsolute).IsLessThanOrEqualTo(budget.GetProperty("pipeline_max_absolute").GetDouble()).Because(s.ToString());
            await Assert.That(s.MedianRelative).IsLessThanOrEqualTo(budget.GetProperty("pipeline_median_relative").GetDouble()).Because(s.ToString());
            await Assert.That(s.P99Relative).IsLessThanOrEqualTo(budget.GetProperty("pipeline_p99_relative").GetDouble()).Because(s.ToString());
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    /// <summary>
    /// The round trip HdrImage → JPEG (base q95 4:4:4, map q95) → ToHdr is at most as far from the HDR input as with
    /// libultrahdr's encoder (ultrahdr_app with the same base and options) times 1.05 plus a margin.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(FileCases))]
    public async Task RoundTrip_IsWithinLibUltraHdrsEncoder(HdrColorSpace space, int scale, int channels)
    {
        Skip.Unless(ReferenceTools.HasUltraHdr, MissingApp);
        using HdrImage hdr = Input("synthetic", space);
        float[] expected = HdrSynthetic.Components(hdr);
        using Image sdr = hdr.ToneMap();
        using Image ours = hdr.ToGainMapImage(sdr, new GainMapOptions { Scale = scale, ChannelCount = channels });
        using Image oursBack = Image.Load(ours.ToJpeg(new JpegEncoderOptions { Quality = 95, Subsampling = JpegSubsampling.Ratio444, GainMap = JpegGainMapMode.Require }), Hdr);
        using HdrImage oursHdr = oursBack.ToHdr(new HdrOptions { ColorSpace = space });

        string stem = Path.Combine(Path.GetTempPath(), $"ps-uhdr-enc-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllBytes(stem + ".hdr.raw", HalfPixels(hdr));
            File.WriteAllBytes(stem + ".sdr.raw", sdr.ToPixelData());
            string nits = (hdr.Headroom * 203f).ToString(CultureInfo.InvariantCulture);
            string args = $"-m 0 -p \"{stem}.hdr.raw\" -a 4 -t 0 -C {(space == HdrColorSpace.LinearSrgb ? 0 : 1)} -y \"{stem}.sdr.raw\" -b 3 -c 0 " +
                $"-w {hdr.Width} -h {hdr.Height} -q 95 -Q 95 -s {scale} -M {(channels == 3 ? 1 : 0)} -D 1 -L {nits} -z \"{stem}.jpg\"";
            int code = ReferenceTools.Run("ultrahdr_app", args, out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_app failed ({code}): {log}");
            using Image theirsBack = Image.Load(stem + ".jpg", Hdr);
            using HdrImage theirsHdr = theirsBack.ToHdr(new HdrOptions { ColorSpace = space });
            HdrUltraHdrOracleTests.Stats o = HdrUltraHdrOracleTests.Measure(oursHdr.GetPixelSpan(), expected, 0, 0);
            HdrUltraHdrOracleTests.Stats t = HdrUltraHdrOracleTests.Measure(theirsHdr.GetPixelSpan(), expected, 0, 0);
            System.Text.Json.JsonElement budget = HdrGainMapGeneratorTests.Budget;
            double factor = budget.GetProperty("libultrahdr_factor").GetDouble();
            await Assert.That(o.MedianRelative).IsLessThanOrEqualTo((t.MedianRelative * factor) + budget.GetProperty("libultrahdr_median_margin").GetDouble())
                .Because($"ours {o}, libultrahdr {t}");
            await Assert.That(o.P99Relative).IsLessThanOrEqualTo((t.P99Relative * factor) + budget.GetProperty("libultrahdr_p99_margin").GetDouble())
                .Because($"ours {o}, libultrahdr {t}");
        }
        finally
        {
            foreach (string suffix in new[] { ".hdr.raw", ".sdr.raw", ".jpg" })
            {
                File.Delete(stem + suffix);
            }
        }
    }
}
