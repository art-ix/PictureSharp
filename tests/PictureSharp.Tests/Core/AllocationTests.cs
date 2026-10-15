using PictureSharp.Internal;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Allocation budgets for encoders that once allocated many times their output (per-tile histograms,
/// a copied subframe per animation candidate, a growing MemoryStream). Single-threaded, so the
/// current-thread counter sees every allocation.
/// </summary>
[Collection(nameof(SerialCollection))]
public class AllocationTests
{
    [Fact]
    public void LosslessWebPEncoding_PoolsHistograms()
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        var options = new WebPEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 };

        // 256×192: 8.4 MB before pooling, about 1.4 MB with it.
        Assert.InRange(Measure(() => image.ToWebP(options)), 0, 3L << 20);
    }

    [Fact]
    public void AnimatedWebPEncoding_ReusesTheSubframe()
    {
        using Image animation = Pan();
        long lossless = Measure(() => animation.ToWebP(new WebPEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 }));
        long lossy = Measure(() => animation.ToWebP(new WebPEncoderOptions { MaxDegreeOfParallelism = 1 }));

        // Eight 256×192 frames: each candidate used to copy its subframe and allocate per-tile histograms.
        Assert.InRange(lossless, 0, 16L << 20);
        Assert.InRange(lossy, 0, 4L << 20);
    }

    [Theory]
    [InlineData("bmp")]
    [InlineData("pcx")]
    [InlineData("png")]
    public void EncodingToBytes_AllocatesAboutTheOutput(string extension)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        EncoderOptions options = extension switch
        {
            "png" => new PngEncoderOptions { MaxDegreeOfParallelism = 1 },
            _ => ImageFormat.FromExtension(extension)!.CreateDefaultEncoderOptions(),
        };
        int length = large.Encode(options).Length;
        long allocated = Measure(() => large.Encode(options));

        // PNG also needs its filtered rows (3 MiB here); MemoryStream growth used to add about twice the output.
        long budget = (2L * length) + (extension == "png" ? 4L << 20 : 1L << 20);
        Assert.InRange(allocated, length, budget);
    }

    [Fact]
    public void GifEncoding_OfAStillImage_StaysBounded()
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        var options = new GifEncoderOptions();

        // 3 MiB of pixels; 61 MB before: canvas copies, the opaque pixel list and an unbounded color cache.
        Assert.InRange(Measure(() => large.Encode(options)), 0, 8L << 20);
    }

    [Theory]
    [InlineData("webp-lossless")]
    [InlineData("webp-lossy")]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("jpeg-progressive")]
    [InlineData("gif")]
    public void Decoding_AllocatesAboutThePixels(string kind)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        byte[] data = large.Encode(kind switch
        {
            "webp-lossless" => new WebPEncoderOptions { Lossless = true, Method = 0 },
            "webp-lossy" => new WebPEncoderOptions(),
            "png" => new PngEncoderOptions(),
            "jpeg" => new JpegEncoderOptions(),
            "jpeg-progressive" => new JpegEncoderOptions { Progressive = true },
            _ => new GifEncoderOptions(),
        });
        var options = new DecoderOptions { MaxDegreeOfParallelism = 1 };

        // The decoded image is 3 MiB; intermediate planes, rows and ARGB buffers come from the pool.
        long allocated = Measure(() =>
        {
            using Image decoded = Image.Load(data, options);
            return decoded;
        });
        Assert.InRange(allocated, 3L << 20, (3L << 20) + (1L << 20));
    }

    [Fact]
    public void PooledBufferStream_JoinsSegments()
    {
        var expected = new byte[3_000_000];
        new Random(5).NextBytes(expected);
        using var stream = new PooledBufferStream();
        stream.WriteByte(expected[0]);
        for (int offset = 1, size = 1; offset < expected.Length; offset += size, size = Math.Min(size * 3, 70_000))
        {
            stream.Write(expected, offset, Math.Min(size, expected.Length - offset));
        }

        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(expected, stream.ToArray());
        using var copy = new MemoryStream();
        stream.WriteTo(copy);
        Assert.Equal(expected, copy.ToArray());
    }

    private static long Measure(Func<object> action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static Image Pan()
    {
        using Image source = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = source.Clone(x => x.Resize(source.Width * 2, source.Height * 2, ResamplingFilter.Bicubic));
        Image animation = large.Clone(x => x.Crop(0, 0, 256, 192));
        for (int i = 1; i < 8; i++)
        {
            using Image frame = large.Clone(x => x.Crop(16 * i, 8 * i, 256, 192));
            animation.Frames.Add(frame, TimeSpan.FromMilliseconds(80));
        }

        return animation;
    }
}
