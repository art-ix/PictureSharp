using PictureSharp.Infrastructure.Codecs.Pcx;

namespace PictureSharp.Tests.Pcx;

public class PcxRleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(126)]
    [InlineData(127)]
    public void RepeatedBytes_SplitAt63AndEscapeHighLiterals(int length)
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
                Assert.Equal(0xFF, encoded[run * 2]);
                Assert.Equal((byte)value, encoded[(run * 2) + 1]);
            }

            int tailBytes = remainder == 0 ? 0 : remainder == 1 && value < 0xC0 ? 1 : 2;
            Assert.Equal((fullRuns * 2) + tailBytes, written);
            if (remainder != 0)
            {
                if (tailBytes == 1)
                {
                    Assert.Equal((byte)value, encoded[fullRuns * 2]);
                }
                else
                {
                    Assert.Equal((byte)(0xC0 | remainder), encoded[fullRuns * 2]);
                    Assert.Equal((byte)value, encoded[(fullRuns * 2) + 1]);
                }
            }

            int offset = 0;
            PcxRle.Decode(encoded.AsSpan(0, written), ref offset, decoded, compressed: true);
            Assert.Equal(written, offset);
            Assert.Equal(source, decoded);
        }
    }

    [Fact]
    public void MixedLiteralsAndRuns_HaveExpectedEncoding()
    {
        byte[] source = [0, 1, 0xBF, 0xC0, 0xC1, 0xFF, 7, 7, 7, 8];
        var encoded = new byte[source.Length * 2];
        int written = PcxRle.Encode(source, encoded);
        Assert.Equal(new byte[] { 0, 1, 0xBF, 0xC1, 0xC0, 0xC1, 0xC1, 0xC1, 0xFF, 0xC3, 7, 8 }, encoded[..written]);
    }

    [Theory]
    [InlineData(0xC0)]
    [InlineData(0xC1)]
    [InlineData(0xFF)]
    public void MissingRunValue_IsRejectedByTheRleDecoder(byte marker)
    {
        byte[] source = [marker];
        var row = new byte[1];
        Assert.Throws<ImageFormatException>(() =>
        {
            int offset = 0;
            PcxRle.Decode(source, ref offset, row, compressed: true);
        });
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public void Decode_InvalidOffset_ThrowsFormatException(int offset, bool compressed)
    {
        byte[] source = [1, 2, 3];
        var row = new byte[1];
        Assert.Throws<ImageFormatException>(() => PcxRle.Decode(source, ref offset, row, compressed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decode_LimitedSpan_DoesNotReadExcludedBytes(bool compressed)
    {
        byte[] source = [1, 2];
        var row = new byte[2];
        Assert.Throws<ImageFormatException>(() =>
        {
            int offset = 0;
            PcxRle.Decode(source.AsSpan(0, 1), ref offset, row, compressed);
        });
    }

    [Fact]
    public void Decode_DoesNotConsumeTheNextScanline()
    {
        byte[] source = [0xC3, 7, 0xC3, 8];
        var row = new byte[3];
        int offset = 0;
        PcxRle.Decode(source, ref offset, row, compressed: true);
        Assert.Equal(2, offset);
        Assert.Equal(new byte[] { 7, 7, 7 }, row);
        PcxRle.Decode(source, ref offset, row, compressed: true);
        Assert.Equal(4, offset);
        Assert.Equal(new byte[] { 8, 8, 8 }, row);
    }
}
