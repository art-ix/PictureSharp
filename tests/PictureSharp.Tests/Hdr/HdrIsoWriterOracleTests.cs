using System.Runtime.InteropServices;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Hdr;

/// <summary>
/// The ISO 21496-1 writer against libultrahdr v2.0.2 through tests/assets/ultrahdr_iso_oracle.cpp: the continued
/// fractions and the encoded payload must be identical. Skipped without the harness (scripts/build-ultrahdr-reference.sh).
/// </summary>
[Property("Category", "Oracle")]
public class HdrIsoWriterOracleTests
{
    private const string Missing = "ultrahdr_iso_oracle is not built (scripts/build-ultrahdr-reference.sh).";

    private static byte[] RunOracle(string mode, byte[] input)
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"uhdr-iso-{Guid.NewGuid():N}.bin"), outputPath = inputPath + ".out";
        try
        {
            File.WriteAllBytes(inputPath, input);
            int code = ReferenceTools.Run("ultrahdr_iso_oracle", $"{mode} \"{inputPath}\" \"{outputPath}\"", out string log);
            if (code != 0) throw new InvalidOperationException($"ultrahdr_iso_oracle failed ({code}): {log}");
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            File.Delete(inputPath);
            File.Delete(outputPath);
        }
    }

    /// <summary>Edge values, values of the corpus and seeded random values over the ranges metadata uses.</summary>
    private static List<float> Values()
    {
        List<float> values =
        [
            0, 1, -1, 0.5f, 1f / 3, 2f / 3, 0.1f, 1e-7f, float.Epsilon, -float.Epsilon, 1.6180339f, MathF.PI, 64, -64,
            2.3219280f, 1e-3f, 2147483520f, 2147483648f, 4294967040f, 4294967296f, 3e9f, float.NaN, float.PositiveInfinity,
            float.NegativeInfinity, float.MaxValue, 0.99999994f, 1.0000001f,
        ];
        var random = new Random(21496);
        for (int i = 0; i < 4000; i++)
        {
            values.Add((float)((random.NextDouble() * 140) - 70));
            values.Add((float)(random.NextDouble() * 8));
            values.Add((float)(random.NextDouble() * 1e-2));
            values.Add(BitConverter.Int32BitsToSingle(random.Next(0, 0x7F800000)));
        }

        return values;
    }

    [Test]
    public async Task Fractions_MatchLibUltraHdr()
    {
        Skip.Unless(ReferenceTools.HasUltraHdrIsoOracle, Missing);
        List<float> values = Values();
        byte[] expected = RunOracle("fractions", MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(values)).ToArray());
        var actual = new MemoryStream();
        using (var writer = new BinaryWriter(actual))
        {
            foreach (float v in values)
            {
                bool uok = GainMapMetadataWriter.FloatToUnsignedFraction(v, out uint un, out uint ud);
                bool sok = GainMapMetadataWriter.FloatToSignedFraction(v, out int sn, out uint sd);
                // The harness writes whatever the outputs hold after a failure; only successes are compared in full.
                writer.Write(uok);
                writer.Write(uok ? un : 0u);
                writer.Write(uok ? ud : 0u);
                writer.Write(sok);
                writer.Write(sok ? sn : 0);
                writer.Write(sok ? sd : 0u);
            }
        }

        byte[] mine = actual.ToArray();
        for (int i = 0; i < values.Count; i++)
        {
            Span<byte> e = expected.AsSpan(i * 18, 18);
            if (e[0] == 0) e[1..9].Clear();
            if (e[9] == 0) e[10..18].Clear();
        }

        int mismatch = Enumerable.Range(0, values.Count).FirstOrDefault(i => !mine.AsSpan(i * 18, 18).SequenceEqual(expected.AsSpan(i * 18, 18)), -1);
        await Assert.That(mismatch == -1 ? "none" : $"{values[mismatch]:R}").IsEqualTo("none");
    }

    [Test]
    public async Task MapIccProfiles_MatchLibUltraHdr()
    {
        Skip.Unless(ReferenceTools.HasUltraHdrIsoOracle, Missing);
        byte[] output = RunOracle("icc", []);
        int pos = 0;
        foreach (HdrColorSpace space in (HdrColorSpace[])[HdrColorSpace.LinearSrgb, HdrColorSpace.LinearDisplayP3])
        {
            int length = BitConverter.ToInt32(output, pos);
            byte[] expected = output[(pos + 4)..(pos + 4 + length)];
            pos += 4 + length;
            await Assert.That(PictureSharp.Algorithms.ColorManagement.IccProfileWriter.WriteSrgbTransfer(space))
                .IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
    }

    /// <summary>Metadata of the corpus and seeded random metadata: one or three channels, both color space flags.</summary>
    private static List<GainMapParameters> Metadata()
    {
        List<GainMapParameters> list = [];
        foreach (string name in HdrIsoWriterTests.IsoCases())
        {
            using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
            (byte[] iso, byte[] icc) = HdrIsoWriterTests.Segments(image.GainMap!.MapData.Span);
            list.Add(GainMapMetadata.ParseIso(iso, 3, icc).Parameters);
        }

        var random = new Random(2102);
        float Next(double min, double max) => (float)(min + (random.NextDouble() * (max - min)));
        for (int i = 0; i < 500; i++)
        {
            int channels = i % 3 == 0 ? 1 : 3;
            float[] Channel(Func<float> f) { float a = f(); return channels == 1 ? [a, a, a] : [a, f(), f()]; }
            float[] min = Channel(() => Next(-4, 0)), max = Channel(() => Next(0, 6));
            list.Add(new GainMapParameters
            {
                Kind = HdrGainMapKinds.Iso21496,
                ChannelCount = channels,
                GainMapMin = i % 7 == 0 ? [0, 0, 0] : min,
                GainMapMax = i % 7 == 0 ? [3, 3, 3] : max,
                Gamma = i % 5 == 0 ? [1, 1, 1] : Channel(() => Next(0.2, 3)),
                BaseOffset = i % 4 == 0 ? [0, 0, 0] : Channel(() => Next(0, 0.1)),
                AlternateOffset = i % 4 == 0 ? [0, 0, 0] : Channel(() => Next(0, 0.1)),
                BaseHeadroom = i % 2 == 0 ? 0 : Next(0, 1),
                AlternateHeadroom = i % 7 == 0 ? 3 : Next(1, 6),
                UseBaseColorSpace = i % 2 == 0,
            });
        }

        return list;
    }

    [Test]
    public async Task Payload_MatchesLibUltraHdr()
    {
        Skip.Unless(ReferenceTools.HasUltraHdrIsoOracle, Missing);
        List<GainMapParameters> metadata = Metadata();
        var input = new MemoryStream();
        using (var writer = new BinaryWriter(input))
        {
            foreach (GainMapParameters p in metadata)
            {
                writer.Write(p.UseBaseColorSpace);
                foreach (float[] a in (float[][])[p.GainMapMax, p.GainMapMin, p.Gamma, p.BaseOffset, p.AlternateOffset])
                {
                    foreach (float v in a) writer.Write(v);
                }

                writer.Write(p.BaseHeadroom);
                writer.Write(p.AlternateHeadroom);
            }
        }

        byte[] output = RunOracle("iso", input.ToArray());
        int pos = 0, mismatches = 0, common = 0, multi = 0;
        foreach (GainMapParameters p in metadata)
        {
            int length = BitConverter.ToInt32(output, pos);
            pos += 4;
            byte[] expected = output[pos..(pos + length)];
            pos += length;
            byte[] actual = GainMapMetadataWriter.WriteIso(p);
            if (!actual.AsSpan().SequenceEqual(expected)) mismatches++;
            if ((actual[4] & 0x08) != 0) common++;
            if ((actual[4] & 0x80) != 0) multi++;
        }

        await Assert.That(mismatches).IsEqualTo(0);
        // Both layouts and both channel counts are covered.
        await Assert.That(common).IsGreaterThan(0);
        await Assert.That(multi).IsGreaterThan(0);
        await Assert.That(multi).IsLessThan(metadata.Count);
    }
}
