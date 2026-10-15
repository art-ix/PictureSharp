using PictureSharp.Infrastructure.Codecs.WebP.Lossless;
using PictureSharp.Processing;

namespace PictureSharp.Tests.WebP;

public class LosslessDecodePrimitiveTests
{
    [Fact]
    public void BackwardReferences_MatchSequentialCopy()
    {
        var random = new Random(44);
        foreach (int distance in new[] { 1, 2, 3, 4, 7, 8, 15, 32, 127 })
            foreach (int length in new[] { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 63, 128, 4096 })
                foreach (int padding in new[] { 0, 1, 2, 3 })
                {
                    int start = distance + padding;
                    uint[] expected = new uint[start + length + 4];
                    random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(expected.AsSpan()));
                    uint[] actual = (uint[])expected.Clone();
                    for (int i = 0; i < length; i++)
                    {
                        expected[start + i] = expected[start + i - distance];
                    }

                    Vp8LDecoder.CopyBlock32b(actual, start, distance, length);
                    Assert.Equal(expected, actual);
                }
    }

    [Fact]
    public void BitReader_MixedReadsMatchIndividualBits()
    {
        var random = new Random(45);
        foreach (int length in Enumerable.Range(1, 80).Append(4096))
            foreach (int offset in new[] { 0, 1, 7 })
            {
                byte[] bytes = new byte[offset + length + 16];
                random.NextBytes(bytes);
                var reader = new Vp8LBitReader(bytes, offset, length);
                var isolated = new Vp8LBitReader(bytes.AsSpan(offset, length).ToArray(), 0, length);
                int position = 0;
                while (position < length * 8)
                {
                    int count = Math.Min(random.Next(1, 25), length * 8 - position);
                    uint expected = 0;
                    for (int bit = 0; bit < count; bit++)
                    {
                        int at = position + bit;
                        expected |= (uint)((bytes[offset + (at >> 3)] >> (at & 7)) & 1) << bit;
                    }

                    Assert.Equal(expected, reader.ReadBits(count));
                    Assert.Equal(expected, isolated.ReadBits(count));
                    Assert.False(reader.IsEndOfStream);
                    position += count;
                }

                // The reader pads its last window, but must eventually report truncation,
                // without consuming bytes past the supplied slice.
                for (int i = 0; i < 4; i++)
                {
                    Assert.Equal(isolated.ReadBits(24), reader.ReadBits(24));
                }

                Assert.True(reader.IsEndOfStream);
            }
    }
}
