using PictureSharp.Infrastructure.Codecs.Pcx;

namespace PictureSharp.Tests.Pcx;

public class PcxRleTests
{
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(62)]
    [Arguments(63)]
    [Arguments(64)]
    [Arguments(65)]
    [Arguments(126)]
    [Arguments(127)]
    public async Task RepeatedBytes_SplitAt63AndEscapeHighLiterals(int length)
    {
        var source = new byte[length];
        var encoded = new byte[length * 2];
        var decoded = new byte[length];
        for (int value = 0; value <= byte.MaxValue; value++)
        {
            source.AsSpan().Fill((byte)value);
            int written = PcxRle.Encode(source, encoded);
            int fullRuns = length / 63;
            int remainder = length % 63;
            for (int run = 0; run < fullRuns; run++)
            {
                await Assert.That((int)(encoded[run * 2])).IsEqualTo(0xFF);
                await Assert.That(encoded[(run * 2) + 1]).IsEqualTo((byte)value);
            }

            int tailBytes = remainder == 0 ? 0 : remainder == 1 && value < 0xC0 ? 1 : 2;
            await Assert.That(written).IsEqualTo((fullRuns * 2) + tailBytes);
            if (remainder != 0)
            {
                if (tailBytes == 1)
                {
                    await Assert.That(encoded[fullRuns * 2]).IsEqualTo((byte)value);
                }
                else
                {
                    await Assert.That(encoded[fullRuns * 2]).IsEqualTo((byte)(0xC0 | remainder));
                    await Assert.That(encoded[(fullRuns * 2) + 1]).IsEqualTo((byte)value);
                }
            }

            int offset = 0;
            PcxRle.Decode(encoded.AsSpan(0, written), ref offset, decoded, compressed: true);
            await Assert.That(offset).IsEqualTo(written);
            await Assert.That(decoded).IsEquivalentTo(source, CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task MixedLiteralsAndRuns_HaveExpectedEncoding()
    {
        byte[] source = [0, 1, 0xBF, 0xC0, 0xC1, 0xFF, 7, 7, 7, 8];
        var encoded = new byte[source.Length * 2];
        int written = PcxRle.Encode(source, encoded);
        await Assert.That(encoded[..written]).IsEquivalentTo(new byte[] { 0, 1, 0xBF, 0xC1, 0xC0, 0xC1, 0xC1, 0xC1, 0xFF, 0xC3, 7, 8 }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0xC0)]
    [Arguments(0xC1)]
    [Arguments(0xFF)]
    public void MissingRunValue_IsRejectedByTheRleDecoder(byte marker)
    {
        byte[] source = [marker];
        var row = new byte[1];
        Assert.ThrowsExactly<ImageFormatException>(() =>
        {
            int offset = 0;
            PcxRle.Decode(source, ref offset, row, compressed: true);
        });
    }

    [Test]
    [Arguments(-1, false)]
    [Arguments(-1, true)]
    [Arguments(4, false)]
    [Arguments(4, true)]
    public void Decode_InvalidOffset_ThrowsFormatException(int offset, bool compressed)
    {
        byte[] source = [1, 2, 3];
        var row = new byte[1];
        Assert.ThrowsExactly<ImageFormatException>(() => PcxRle.Decode(source, ref offset, row, compressed));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Decode_LimitedSpan_DoesNotReadExcludedBytes(bool compressed)
    {
        byte[] source = [1, 2];
        var row = new byte[2];
        Assert.ThrowsExactly<ImageFormatException>(() =>
        {
            int offset = 0;
            PcxRle.Decode(source.AsSpan(0, 1), ref offset, row, compressed);
        });
    }

    [Test]
    public async Task Decode_DoesNotConsumeTheNextScanline()
    {
        byte[] source = [0xC3, 7, 0xC3, 8];
        var row = new byte[3];
        int offset = 0;
        PcxRle.Decode(source, ref offset, row, compressed: true);
        await Assert.That(offset).IsEqualTo(2);
        await Assert.That(row).IsEquivalentTo(new byte[] { 7, 7, 7 }, CollectionOrdering.Matching);
        PcxRle.Decode(source, ref offset, row, compressed: true);
        await Assert.That(offset).IsEqualTo(4);
        await Assert.That(row).IsEquivalentTo(new byte[] { 8, 8, 8 }, CollectionOrdering.Matching);
    }
}
