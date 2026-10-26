using System.Buffers.Binary;
using PictureSharp.Algorithms.Hdr;
using PictureSharp.Domain.Hdr;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Hdr;

/// <summary><c>GainMapMetadata.ToIso()</c>: the ISO 21496-1 payload written back from parsed metadata.</summary>
public class HdrIsoWriterTests
{
    private static ReadOnlySpan<byte> IsoPrefix => "urn:iso:std:iso:ts:21496:-1\0"u8;
    private static ReadOnlySpan<byte> IccPrefix => "ICC_PROFILE\0"u8;

    /// <summary>The corpus files whose attached map is described by ISO 21496-1 metadata.</summary>
    public static IEnumerable<string> IsoCases() =>
        HdrGainMapTests.ValidCases().Where(name =>
        {
            using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
            return image.AttachedGainMap?.Parameters?.Kind == HdrGainMapKinds.Iso21496;
        });

    /// <summary>The ISO payload and the (single-segment) ICC profile of a gain map JPEG's APP2 segments.</summary>
    internal static (byte[] Iso, byte[] Icc) Segments(ReadOnlySpan<byte> jpeg)
    {
        byte[] iso = [], icc = [];
        int pos = 2;
        while (pos + 4 <= jpeg.Length && jpeg[pos] == 0xFF && jpeg[pos + 1] is not (0xDA or 0xD9))
        {
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg[(pos + 2)..]);
            ReadOnlySpan<byte> payload = jpeg.Slice(pos + 4, length - 2);
            if (jpeg[pos + 1] == 0xE2 && payload.StartsWith(IsoPrefix)) iso = payload[IsoPrefix.Length..].ToArray();
            if (jpeg[pos + 1] == 0xE2 && payload.StartsWith(IccPrefix)) icc = payload[(IccPrefix.Length + 2)..].ToArray();
            pos += 2 + length;
        }

        return (iso, icc);
    }

    private static GainMapMetadata Parse(string name, out byte[] iso, out byte[] icc)
    {
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
        (iso, icc) = Segments(image.AttachedGainMap!.MapData.Span);
        return GainMapMetadata.ParseIso(iso, 3, icc);
    }

    private static async Task AssertSameValues(GainMapParameters actual, GainMapParameters expected)
    {
        await Assert.That(actual.GainMapMin).IsEquivalentTo(expected.GainMapMin, CollectionOrdering.Matching);
        await Assert.That(actual.GainMapMax).IsEquivalentTo(expected.GainMapMax, CollectionOrdering.Matching);
        await Assert.That(actual.Gamma).IsEquivalentTo(expected.Gamma, CollectionOrdering.Matching);
        await Assert.That(actual.BaseOffset).IsEquivalentTo(expected.BaseOffset, CollectionOrdering.Matching);
        await Assert.That(actual.AlternateOffset).IsEquivalentTo(expected.AlternateOffset, CollectionOrdering.Matching);
        await Assert.That(actual.BaseHeadroom).IsEqualTo(expected.BaseHeadroom);
        await Assert.That(actual.AlternateHeadroom).IsEqualTo(expected.AlternateHeadroom);
        await Assert.That(actual.UseBaseColorSpace).IsEqualTo(expected.UseBaseColorSpace);
        await Assert.That(actual.MultiplicationSpace).IsEqualTo(expected.MultiplicationSpace);
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task ToIso_ParsesBackToTheSameValues(string name)
    {
        GainMapMetadata metadata = Parse(name, out _, out byte[] icc);
        GainMapMetadata again = GainMapMetadata.ParseIso(metadata.ToIso(), 3, icc);
        await AssertSameValues(again.Parameters, metadata.Parameters);
        await Assert.That(again.ToIso()).IsEquivalentTo(metadata.ToIso(), CollectionOrdering.Matching);
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task ToIso_KeepsTheColorSpaceFlag_OfAnAttachedMap(string name)
    {
        using Image image = Image.Load(HdrGainMapTests.File(name + ".jpg"), new DecoderOptions { LoadGainMap = true });
        (byte[] iso, _) = Segments(image.AttachedGainMap!.MapData.Span);
        // Loading fills MultiplicationSpace with the base primaries; the written flag must still be the file's.
        byte[] written = GainMapMetadataWriter.WriteIso(image.AttachedGainMap.Parameters!);
        await Assert.That(written[4] & 0x40).IsEqualTo(iso[4] & 0x40);
    }

    [Test]
    public async Task MultiplicationSpace_IsNull_WhenTheBaseColorSpaceIsUsed()
    {
        await Assert.That(Parse("iso_mono_srgb", out _, out _).MultiplicationSpace).IsNull();
        await Assert.That(Parse("iso_alt_space", out _, out _).MultiplicationSpace).IsNotNull();
    }

    [Test]
    [MethodDataSource(nameof(IsoCases))]
    public async Task ToAlternateIcc_ParsesBackToTheSameSpace(string name)
    {
        GainMapMetadata metadata = Parse(name, out _, out _);
        byte[]? icc = metadata.ToAlternateIcc();
        await Assert.That(icc is null).IsEqualTo(metadata.MultiplicationSpace is null).Because(name);
        GainMapMetadata again = GainMapMetadata.ParseIso(metadata.ToIso(), 3, icc);
        await Assert.That(again.MultiplicationSpace).IsEqualTo(metadata.MultiplicationSpace).Because(name);
    }

    [Test]
    public async Task ToIso_WritesOneChannel_WhenAllChannelsAreEqual()
    {
        var parameters = new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            ChannelCount = 3,
            GainMapMax = [2.5f, 2.5f, 2.5f],
            Gamma = [1, 1, 1],
            AlternateHeadroom = 2.5f,
        };
        byte[] single = GainMapMetadataWriter.WriteIso(parameters);
        await Assert.That(single[4] & 0x80).IsEqualTo(0);

        byte[] multi = GainMapMetadataWriter.WriteIso(parameters with { GainMapMax = [2.5f, 2.25f, 2.5f] });
        await Assert.That(multi[4] & 0x80).IsEqualTo(0x80);
        await Assert.That(GainMapMetadata.ParseIso(multi, 3).Parameters.GainMapMax).IsEquivalentTo([2.5f, 2.25f, 2.5f], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ToIso_UsesACommonDenominator_WhenAllFractionsShareIt()
    {
        var parameters = new GainMapParameters
        {
            Kind = HdrGainMapKinds.Iso21496,
            GainMapMin = [0, 0, 0],
            GainMapMax = [3, 3, 3],
            Gamma = [1, 1, 1],
            AlternateHeadroom = 3,
        };
        byte[] common = GainMapMetadataWriter.WriteIso(parameters);
        await Assert.That(common[4]).IsEqualTo((byte)(0x40 | 0x08));
        await Assert.That(common.Length).IsEqualTo(5 + 12 + 20);
        byte[] separate = GainMapMetadataWriter.WriteIso(parameters with { GainMapMax = [3.5f, 3.5f, 3.5f] });
        await Assert.That(separate[4] & 0x08).IsEqualTo(0);
        await Assert.That(separate.Length).IsEqualTo(5 + 16 + 40);
    }

    [Test]
    public async Task ToIso_Throws_ForAppleMetadata()
    {
        var apple = new GainMapParameters { Kind = HdrGainMapKinds.Apple, AlternateHeadroom = 1 };
        await Assert.That(() => GainMapMetadataWriter.WriteIso(apple)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task ToIso_Throws_ForAValueWithoutAFraction()
    {
        var parameters = new GainMapParameters { Kind = HdrGainMapKinds.Iso21496, Gamma = [float.NaN, 1, 1], AlternateHeadroom = 1 };
        await Assert.That(() => GainMapMetadataWriter.WriteIso(parameters)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(0f, 0u, 1u)]
    [Arguments(0.5f, 1u, 2u)]
    [Arguments(2.25f, 9u, 4u)]
    [Arguments(7f, 7u, 1u)]
    public async Task FloatToUnsignedFraction_FindsExactFractions(float value, uint numerator, uint denominator)
    {
        await Assert.That(GainMapMetadataWriter.FloatToUnsignedFraction(value, out uint n, out uint d)).IsTrue();
        await Assert.That((n, d)).IsEqualTo((numerator, denominator));
    }

    [Test]
    [Arguments(float.NaN)]
    [Arguments(-1f)]
    [Arguments(float.PositiveInfinity)]
    [Arguments(5e9f)]
    public async Task FloatToUnsignedFraction_Rejects(float value) =>
        await Assert.That(GainMapMetadataWriter.FloatToUnsignedFraction(value, out _, out _)).IsFalse();

    [Test]
    public async Task FloatToSignedFraction_KeepsTheSign()
    {
        await Assert.That(GainMapMetadataWriter.FloatToSignedFraction(-0.75f, out int n, out uint d)).IsTrue();
        await Assert.That((n, d)).IsEqualTo((-3, 4u));
        await Assert.That(GainMapMetadataWriter.FloatToSignedFraction(3e9f, out _, out _)).IsFalse();
    }
}
