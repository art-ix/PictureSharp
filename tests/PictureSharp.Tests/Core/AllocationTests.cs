using PictureSharp.Composition;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Primitives.Memory;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;
using TUnit.Core.Enums;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Allocation budgets for encoders that once allocated many times their output (per-tile histograms,
/// a copied subframe per animation candidate, a growing MemoryStream). Single-threaded, so the
/// current-thread counter sees every allocation. The counter is per thread, so these run one at a time but
/// alongside other tests, and first because the animation case is the longest serial step of the run.
/// </summary>
[NotInParallel("Allocations")]
[ExecutionPriority(Priority.High)]
public class AllocationTests
{
    // Alone: other tests renting large buffers from the shared ArrayPool at the same time made the encoder allocate
    // its histograms again on a 4-core CI runner (4.8 MB); see also Measure.
    [Test]
    [NotInParallel]
    public async Task LosslessWebPEncoding_PoolsHistograms()
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        var options = new WebPEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 };

        // 256×192: 8.4 MB before pooling, about 1.4 MB with it.
        await Assert.That(Measure(() => image.ToWebP(options))).IsBetween(0L, 3L << 20);
    }

    [Test]
    [NotInParallel]
    [ExecutionPriority(Priority.Low)]
    public async Task AnimatedWebPEncoding_ReusesTheSubframe()
    {
        using Image animation = Pan();
        long lossless = Measure(() => animation.ToWebP(new WebPEncoderOptions { Lossless = true, MaxDegreeOfParallelism = 1 }));
        long lossy = Measure(() => animation.ToWebP(new WebPEncoderOptions { MaxDegreeOfParallelism = 1 }));

        // Eight 256×192 frames: each candidate used to copy its subframe and allocate per-tile histograms.
        await Assert.That(lossless).IsBetween(0L, 16L << 20);
        await Assert.That(lossy).IsBetween(0L, 4L << 20);
    }

    [Test]
    public async Task HdrResize_AllocatesTheMapNotFullSizeFloatBuffers()
    {
        using Image source = Image.Load(Corpus.PathOf("hdr/iso_mono_srgb.jpg"), new DecoderOptions { LoadGainMap = true });
        using Image hdr = source.Clone(x => x.Resize(1536, 1024));   // base 1536×1024, map 768×512 (edited)
        using Image sdr = hdr.Clone();
        sdr.GainMap = null;
        long withMap = Measure(() => { Image r = hdr.Clone(x => x.Resize(768, 512, ResamplingFilter.Lanczos3)); r.Dispose(); return r; });
        long withoutMap = Measure(() => { Image r = sdr.Clone(x => x.Resize(768, 512, ResamplingFilter.Lanczos3)); r.Dispose(); return r; });

        // The map resize costs about its own result (384×256, 0.39 MB) and coefficients: 0.44 MB measured on this
        // thread. One float buffer of the base would be 25 MB.
        await Assert.That(withMap - withoutMap).IsBetween(0L, 2L << 20);
    }

    [Test]
    [Arguments("bmp")]
    [Arguments("pcx")]
    [Arguments("png")]
    public async Task EncodingToBytes_AllocatesAboutTheOutput(string extension)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        EncoderOptions options = extension switch
        {
            "png" => new PngEncoderOptions { MaxDegreeOfParallelism = 1 },
            _ => LibraryServices.Codecs.GetEncoder(ImageFormat.FromExtension(extension)!)!.CreateDefaultOptions(),
        };
        int length = large.Encode(options).Length;
        long allocated = Measure(() => large.Encode(options));

        // PNG also needs its filtered rows (3 MiB here); MemoryStream growth used to add about twice the output.
        long budget = (2L * length) + (extension == "png" ? 4L << 20 : 1L << 20);
        await Assert.That(allocated).IsBetween((long)length, budget);
    }

    [Test]
    public async Task GifEncoding_OfAStillImage_StaysBounded()
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        var options = new GifEncoderOptions();

        // 3 MiB of pixels; 61 MB before: canvas copies, the opaque pixel list and an unbounded color cache.
        await Assert.That(Measure(() => large.Encode(options))).IsBetween(0L, 8L << 20);
    }

    [Test]
    [Arguments("webp-lossless")]
    [Arguments("webp-lossy")]
    [Arguments("png")]
    [Arguments("jpeg")]
    [Arguments("jpeg-progressive")]
    [Arguments("gif")]
    public async Task Decoding_AllocatesAboutThePixels(string kind)
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
        await Assert.That(allocated).IsBetween(3L << 20, (3L << 20) + (1L << 20));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task JpegTargetSize_DoesNotAllocateTheFullImage(bool progressive)
    {
        using Image image = Image.Load(Corpus.PathOf("src/photo.png"));
        using Image large = image.Clone(x => x.Resize(1024, 768));
        byte[] data = large.Encode(new JpegEncoderOptions { Progressive = progressive });
        var options = new DecoderOptions { MaxDegreeOfParallelism = 1, TargetSize = new(128, 128) };

        // A 1/4 decode is 256x192 (192 KiB of pixels) resized to 128x96; the full decode alone would be 3 MiB.
        (int Width, int Height) size = default;
        long allocated = Measure(() =>
        {
            using Image decoded = Image.Load(data, options);
            size = (decoded.Width, decoded.Height);
            return decoded;
        });
        await Assert.That(size).IsEqualTo((128, 96));
        await Assert.That(allocated).IsBetween(0L, 512L << 10);
    }

    [Test]
    public async Task PooledBufferStream_JoinsSegments()
    {
        var expected = new byte[3_000_000];
        new Random(5).NextBytes(expected);
        using var stream = new PooledBufferStream();
        stream.WriteByte(expected[0]);
        for (int offset = 1, size = 1; offset < expected.Length; offset += size, size = Math.Min(size * 3, 70_000))
        {
            stream.Write(expected, offset, Math.Min(size, expected.Length - offset));
        }

        await Assert.That(stream.Length).IsEqualTo(expected.Length);
        await Assert.That(stream.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        using var copy = new MemoryStream();
        stream.WriteTo(copy);
        await Assert.That(copy.ToArray()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    // The minimum of a few runs: ArrayPool.Shared drops its arrays on a gen2 GC (memory pressure from other tests),
    // and the run after that rents everything anew (4.8 MB instead of 1.4 MB for the lossless WebP histograms).
    private static long Measure(Func<object> action, int runs = 3)
    {
        action();
        long minimum = long.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return minimum;
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
