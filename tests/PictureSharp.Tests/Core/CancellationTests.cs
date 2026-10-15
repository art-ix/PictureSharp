using System.Diagnostics;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Cancellation before and during reading, decoding, encoding and processing. Work in flight is canceled from another
/// thread once it has run for a fraction of its normal duration, and must stop promptly.
/// </summary>
public class CancellationTests
{
    private static readonly CancellationToken Canceled = new(canceled: true);

    public static TheoryData<string> CorpusFiles() => [.. Directory.EnumerateFiles(Corpus.CorpusDir, "*", SearchOption.AllDirectories)
        .Where(p => Path.GetExtension(p) is not ".bin")
        .Select(p => Path.GetRelativePath(Corpus.CorpusDir, p).Replace('\\', '/'))
        .Order(StringComparer.Ordinal)];

    public static TheoryData<string> Formats() => ["webp", "webp-lossless", "png", "jpeg", "gif", "bmp", "pcx", "ico"];

    // ------------------------------------------------------------------ before the start

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void CanceledToken_StopsEveryLoad(string file)
    {
        byte[] data = Corpus.Read(file);
        Assert.Throws<OperationCanceledException>(() => ImageReader.Load(data, null, Canceled));
        Assert.Throws<OperationCanceledException>(() => ImageReader.Load(new NonSeekableStream(data), null, Canceled));
        Assert.Throws<OperationCanceledException>(() => ImageReader.Load(Corpus.PathOf(file), null, Canceled));
    }

    [Fact]
    public async Task CanceledToken_StopsAsyncPaths()
    {
        string path = Corpus.PathOf("png/rgba8.png");
        using Image image = ImageReader.Load(path);
        string output = Path.Combine(Path.GetTempPath(), $"picturesharp-cancel-{Guid.NewGuid():N}.png");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageReader.LoadAsync(path, Canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageReader.LoadAsync(new NonSeekableStream(File.ReadAllBytes(path)), Canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageReader.IdentifyAsync(path, Canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageWriter.SaveAsync(image, output, Canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageWriter.SaveAsync(image, new MemoryStream(), ImageFormat.Png, Canceled));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => IcoFile.SaveAsync([image], new MemoryStream(), null, Canceled));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void CanceledToken_StopsEverySave(string format)
    {
        using Image image = Pattern(32, 32);
        EncoderOptions options = Options(format);
        var stream = new MemoryStream();
        Assert.Throws<OperationCanceledException>(() => ImageWriter.Save(image, stream, options, Canceled));
        Assert.Equal(0, stream.Length);
        Assert.Throws<OperationCanceledException>(() => ImageWriter.Encode(image, options, Canceled));

        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-cancel-{Guid.NewGuid():N}");
        Assert.Throws<OperationCanceledException>(() => ImageWriter.Save(image, path, options, Canceled));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CanceledToken_StopsProcessingBeforeAnyChange()
    {
        using Image image = Pattern(16, 16);
        Rgba32[] before = image.GetPixelSpan().ToArray();
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x => x.Grayscale(), Canceled));
        Assert.Equal(before, image.GetPixelSpan().ToArray());
        Assert.Throws<OperationCanceledException>(() => image.Clone(x => x.Resize(8, 8), Canceled));
        Assert.Throws<OperationCanceledException>(() => IcoFile.LoadAll(image.ToIco(), null, Canceled));
    }

    [Fact]
    public void Mutate_StopsBetweenOperations_AndKeepsFinishedOnes()
    {
        using Image image = Pattern(16, 16);
        using var source = new CancellationTokenSource();
        bool reachedThird = false;
        Assert.Throws<OperationCanceledException>(() => image.Mutate(x =>
        {
            x.Resize(8, 8);
            x.Apply(_ => source.Cancel());
            reachedThird = true;
            x.Grayscale();
        }, source.Token));

        Assert.True(reachedThird);
        Assert.Equal(new System.Drawing.Size(8, 8), image.Size);
        Assert.NotEqual(image.GetPixelSpan()[1].R, image.GetPixelSpan()[1].G);
    }

    [Fact]
    public void Mutate_ExposesTheTokenToCustomOperations()
    {
        using Image image = Pattern(4, 4);
        using var source = new CancellationTokenSource();
        CancellationToken seen = default;
        image.Mutate(x => seen = x.CancellationToken, source.Token);
        Assert.Equal(source.Token, seen);
        image.Mutate(x => seen = x.CancellationToken);
        Assert.False(seen.CanBeCanceled);
    }

    [Fact]
    public void Clone_DisposesThePartialCopyAndKeepsTheSource()
    {
        using Image image = Animated(8, 8, frames: 3);
        Rgba32[] before = image.Frames[2].GetPixelSpan().ToArray();
        using var source = new CancellationTokenSource();
        int frames = 0;
        Assert.Throws<OperationCanceledException>(() => image.Clone(x => x.Apply(_ =>
        {
            if (++frames == 2)
            {
                source.Cancel();
            }
        }).Invert(), source.Token));
        Assert.Equal(2, frames);
        Assert.Equal(before, image.Frames[2].GetPixelSpan().ToArray());
    }

    internal static EncoderOptions Options(string format) => format switch
    {
        "webp" => new WebPEncoderOptions(),
        "webp-lossless" => new WebPEncoderOptions { Lossless = true },
        "png" => new PngEncoderOptions(),
        "jpeg" => new JpegEncoderOptions(),
        "gif" => new GifEncoderOptions(),
        "bmp" => new BmpEncoderOptions(),
        "pcx" => new PcxEncoderOptions(),
        _ => new IcoEncoderOptions(),
    };

    internal static Image Pattern(int width, int height)
    {
        var pixels = new Rgba32[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)(i * 3), (byte)(i * 5), (byte)(i * 11));
        }

        return Image.LoadPixelData(pixels, width, height);
    }

    internal static Image Noise(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        byte[] bytes = new byte[width * height * 4];
        random.NextBytes(bytes);
        for (int i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return Image.LoadPixelData(bytes, width, height, PixelFormat.Rgba32);
    }

    internal static Image Animated(int width, int height, int frames, bool noise = false)
    {
        Image image = noise ? Noise(width, height) : Pattern(width, height);
        for (int i = 1; i < frames; i++)
        {
            using Image frame = noise ? Noise(width, height, seed: i + 1) : Pattern(width, height);
            image.Frames.Add(frame, TimeSpan.FromMilliseconds(50));
        }

        return image;
    }
}

/// <summary>
/// Work canceled while in flight. Runs alone: the timing must not compete with other tests, and the large images would
/// otherwise drain the shared array pool that the allocation tests measure.
/// </summary>
[Collection(nameof(SerialCollection))]
public class CancellationInFlightTests
{
    private static EncoderOptions Options(string format) => CancellationTests.Options(format);

    private static Image Noise(int width, int height, int seed = 1) => CancellationTests.Noise(width, height, seed);

    private static Image Animated(int width, int height, int frames, bool noise = false) =>
        CancellationTests.Animated(width, height, frames, noise);

    // ------------------------------------------------------------------ during the work

    [Theory]
    [InlineData("webp")]
    [InlineData("webp-lossless")]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("jpeg-progressive")]
    [InlineData("gif")]
    [InlineData("bmp")]
    public void Decode_StopsPromptlyWhenCanceled(string format)
    {
        int size = format is "gif" or "bmp" ? 4096 : 3072;
        using Image source = Noise(size, size);
        byte[] data = format switch
        {
            "jpeg-progressive" => ImageWriter.Encode(source, new JpegEncoderOptions { Progressive = true }),
            "png" => ImageWriter.Encode(source, new PngEncoderOptions { CompressionLevel = 1 }),
            _ => ImageWriter.Encode(source, Options(format)),
        };
        AssertCancelsPromptly(token => ImageReader.Load(data, null, token).Dispose());
    }

    [Theory]
    [InlineData("webp")]
    [InlineData("webp-lossless")]
    [InlineData("png")]
    [InlineData("jpeg")]
    [InlineData("gif")]
    public void Encode_StopsPromptlyWhenCanceled(string format)
    {
        int size = format is "webp-lossless" or "gif" ? 1024 : 2048;
        using Image source = Noise(size, size);
        EncoderOptions options = format == "png" ? new PngEncoderOptions { CompressionLevel = 9 } : Options(format);
        AssertCancelsPromptly(token => ImageWriter.Encode(source, options, token));
    }

    [Fact]
    public void Animation_StopsBetweenFrames()
    {
        using Image source = Animated(512, 512, frames: 12, noise: true);
        byte[] webp = source.ToWebP();
        AssertCancelsPromptly(token => ImageWriter.Encode(source, new WebPEncoderOptions(), token));
        AssertCancelsPromptly(token => ImageReader.Load(webp, null, token).Dispose());
    }

    [Theory]
    [InlineData("resize")]
    [InlineData("blur")]
    [InlineData("rotate")]
    [InlineData("sharpen")]
    public void Processing_StopsPromptlyWhenCanceled(string operation)
    {
        using Image source = Noise(3072, 3072);
        AssertCancelsPromptly(token => source.Clone(x => _ = operation switch
        {
            "resize" => x.Resize(3000, 2900, ResamplingFilter.Lanczos3),
            "blur" => x.GaussianBlur(12f),
            "rotate" => x.Rotate(13f, ResamplingFilter.Bicubic),
            _ => x.Sharpen(),
        }, token).Dispose());
    }

    /// <summary>
    /// Measures the uncanceled duration, then cancels a second run at a quarter of it: the run must end with
    /// <see cref="OperationCanceledException"/> well before it would have finished.
    /// </summary>
    private static void AssertCancelsPromptly(Action<CancellationToken> work)
    {
        var watch = Stopwatch.StartNew();
        work(CancellationToken.None);
        TimeSpan full = watch.Elapsed;

        using var source = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        Task run = Task.Run(() =>
        {
            started.Set();
            work(source.Token);
        });
        started.Wait();
        Thread.Sleep(full / 4);
        watch.Restart();
        source.Cancel();
        Exception? error = Record.Exception(() => run.GetAwaiter().GetResult());
        TimeSpan latency = watch.Elapsed;

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.True(latency < TimeSpan.FromMilliseconds(250) + (full / 2),
            $"Stopped {latency.TotalMilliseconds:F0} ms after cancellation; the full run took {full.TotalMilliseconds:F0} ms.");
    }
}
