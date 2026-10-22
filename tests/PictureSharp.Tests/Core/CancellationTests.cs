using System.Diagnostics;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Ico;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Pcx;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
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

    public static TestCases<string> Formats() => ["webp", "webp-lossless", "png", "jpeg", "gif", "bmp", "pcx", "ico"];

    // ------------------------------------------------------------------ before the start

    [Test]
    [MethodDataSource(typeof(Corpus), nameof(Corpus.Representatives))]
    public void CanceledToken_StopsEveryLoad(string file)
    {
        byte[] data = Corpus.Read(file);
        Assert.ThrowsExactly<OperationCanceledException>(() => Image.Load(data, null, Canceled));
        Assert.ThrowsExactly<OperationCanceledException>(() => Image.Load(new NonSeekableStream(data), null, Canceled));
        Assert.ThrowsExactly<OperationCanceledException>(() => Image.Load(Corpus.PathOf(file), null, Canceled));
    }

    [Test]
    public async Task CanceledToken_StopsAsyncPaths()
    {
        string path = Corpus.PathOf("png/rgba8.png");
        using Image image = Image.Load(path);
        string output = Path.Combine(Path.GetTempPath(), $"picturesharp-cancel-{Guid.NewGuid():N}.png");
        await Assert.ThrowsAsync<OperationCanceledException>(() => Image.LoadAsync(path, Canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Image.LoadAsync(new NonSeekableStream(File.ReadAllBytes(path)), Canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Image.IdentifyAsync(path, Canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => image.SaveAsync(output, Canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => image.SaveAsync(new MemoryStream(), ImageFormat.Png, Canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() => IcoFile.SaveAsync([image], new MemoryStream(), null, Canceled));
        await Assert.That(File.Exists(output)).IsFalse();
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task CanceledToken_StopsEverySave(string format)
    {
        using Image image = Pattern(32, 32);
        EncoderOptions options = Options(format);
        var stream = new MemoryStream();
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Save(stream, options, Canceled));
        await Assert.That(stream.Length).IsEqualTo(0);
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Encode(options, Canceled));

        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-cancel-{Guid.NewGuid():N}");
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Save(path, options, Canceled));
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task CanceledToken_StopsProcessingBeforeAnyChange()
    {
        using Image image = Pattern(16, 16);
        Rgba32[] before = image.GetPixelSpan().ToArray();
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Mutate(x => x.Grayscale(), Canceled));
        await Assert.That(image.GetPixelSpan().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Clone(x => x.Resize(8, 8), Canceled));
        Assert.ThrowsExactly<OperationCanceledException>(() => IcoFile.LoadAll(image.ToIco(), null, Canceled));
    }

    [Test]
    public async Task Mutate_StopsBetweenOperations_AndKeepsFinishedOnes()
    {
        using Image image = Pattern(16, 16);
        using var source = new CancellationTokenSource();
        bool reachedThird = false;
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Mutate(x =>
        {
            x.Resize(8, 8);
            x.Apply(_ => source.Cancel());
            reachedThird = true;
            x.Grayscale();
        }, source.Token));

        await Assert.That(reachedThird).IsTrue();
        await Assert.That(image.Size).IsEqualTo(new System.Drawing.Size(8, 8));
        await Assert.That(image.GetPixelSpan()[1].G).IsNotEqualTo(image.GetPixelSpan()[1].R);
    }

    [Test]
    public async Task Mutate_ExposesTheTokenToCustomOperations()
    {
        using Image image = Pattern(4, 4);
        using var source = new CancellationTokenSource();
        CancellationToken seen = default;
        image.Mutate(x => seen = x.CancellationToken, source.Token);
        await Assert.That(seen).IsEqualTo(source.Token);
        image.Mutate(x => seen = x.CancellationToken);
        await Assert.That(seen.CanBeCanceled).IsFalse();
    }

    [Test]
    public async Task Clone_DisposesThePartialCopyAndKeepsTheSource()
    {
        using Image image = Animated(8, 8, frames: 3);
        Rgba32[] before = image.Frames[2].GetPixelSpan().ToArray();
        using var source = new CancellationTokenSource();
        int frames = 0;
        Assert.ThrowsExactly<OperationCanceledException>(() => image.Clone(x => x.Apply(_ =>
        {
            if (++frames == 2)
            {
                source.Cancel();
            }
        }).Invert(), source.Token));
        await Assert.That(frames).IsEqualTo(2);
        await Assert.That(image.Frames[2].GetPixelSpan().ToArray()).IsEquivalentTo(before, CollectionOrdering.Matching);
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
[NotInParallel]
[Property("Category", "Slow")]
public class CancellationInFlightTests
{
    private static EncoderOptions Options(string format) => CancellationTests.Options(format);

    private static Image Noise(int width, int height, int seed = 1) => CancellationTests.Noise(width, height, seed);

    private static Image Animated(int width, int height, int frames, bool noise = false) =>
        CancellationTests.Animated(width, height, frames, noise);

    // ------------------------------------------------------------------ during the work

    [Test]
    [Arguments("webp")]
    [Arguments("webp-lossless")]
    [Arguments("png")]
    [Arguments("jpeg")]
    [Arguments("jpeg-progressive")]
    [Arguments("gif")]
    [Arguments("bmp")]
    public async Task Decode_StopsPromptlyWhenCanceled(string format)
    {
        int size = format is "gif" or "bmp" ? 4096 : 3072;
        using Image source = Noise(size, size);
        byte[] data = format switch
        {
            "jpeg-progressive" => source.Encode(new JpegEncoderOptions { Progressive = true }),
            "png" => source.Encode(new PngEncoderOptions { CompressionLevel = 1 }),
            _ => source.Encode(Options(format)),
        };
        await AssertCancelsPromptly(token => Image.Load(data, null, token).Dispose());
    }

    [Test]
    [Arguments("webp")]
    [Arguments("webp-lossless")]
    [Arguments("png")]
    [Arguments("jpeg")]
    [Arguments("gif")]
    public async Task Encode_StopsPromptlyWhenCanceled(string format)
    {
        int size = format is "webp-lossless" or "gif" ? 1024 : 2048;
        using Image source = Noise(size, size);
        EncoderOptions options = format == "png" ? new PngEncoderOptions { CompressionLevel = 9 } : Options(format);
        await AssertCancelsPromptly(token => source.Encode(options, token));
    }

    [Test]
    public async Task Animation_StopsBetweenFrames()
    {
        using Image source = Animated(512, 512, frames: 12, noise: true);
        byte[] webp = source.ToWebP();
        await AssertCancelsPromptly(token => source.Encode(new WebPEncoderOptions(), token));
        await AssertCancelsPromptly(token => Image.Load(webp, null, token).Dispose());
    }

    [Test]
    [Arguments("resize")]
    [Arguments("blur")]
    [Arguments("rotate")]
    [Arguments("sharpen")]
    [Arguments("vibrance")]
    [Arguments("vignette")]
    [Arguments("shadows")]
    [Arguments("clarity")]
    [Arguments("denoise")]
    [Arguments("dust")]
    [Arguments("chroma")]
    [Arguments("negative")]
    public async Task Processing_StopsPromptlyWhenCanceled(string operation)
    {
        using Image source = Noise(3072, 3072);
        await AssertCancelsPromptly(token => source.Clone(x => _ = operation switch
        {
            "resize" => x.Resize(3000, 2900, ResamplingFilter.Lanczos3),
            "blur" => x.GaussianBlur(12f),
            "rotate" => x.Rotate(13f, ResamplingFilter.Bicubic),
            "vibrance" => x.Vibrance(0.5f),
            "vignette" => x.Vignette(0.5f),
            "shadows" => x.ShadowsHighlights(0.5f, -0.5f),
            "clarity" => x.Clarity(0.5f),
            "denoise" => x.Denoise(0.5f, 4),
            "dust" => x.RemoveDust(8),
            "chroma" => x.ReduceColorNoise(),
            "negative" => x.DevelopNegative(),
            _ => x.Sharpen(),
        }, token).Dispose());
    }

    [Test]
    [Arguments("mask")]
    [Arguments("normal")]
    [Arguments("linear")]
    [Arguments("multiply")]
    [Arguments("levels")]
    [Arguments("autocontrast")]
    [Arguments("equalize")]
    [Arguments("autowb")]
    public async Task Graphics_StopsPromptlyInsideSingleFrame(string operation)
    {
        using Image target = Noise(4096, 4096);
        using Image source = Noise(4096, 4096, seed: 2);
        await AssertCancelsPromptly(token => target.Mutate(x => _ = operation switch
        {
            "mask" => x.ApplyMask(source, new MaskOptions { Channel = MaskChannel.Luminance }),
            "normal" => x.DrawImage(source, System.Drawing.Point.Empty, 0.5f),
            "linear" => x.DrawImage(source, System.Drawing.Point.Empty, new DrawImageOptions { LinearLight = true, Opacity = 0.5f }),
            "multiply" => x.DrawImage(source, System.Drawing.Point.Empty, new DrawImageOptions { BlendMode = BlendMode.Multiply }),
            "levels" => x.Gamma(1.2f),
            "autocontrast" => x.AutoContrast(1f),
            "autowb" => x.AutoWhiteBalance(),
            _ => x.Equalize(),
        }, token));
    }

    /// <summary>
    /// Measures the uncanceled duration, then cancels a second run at a quarter of it: the run must end with
    /// <see cref="OperationCanceledException"/> well before it would have finished.
    /// </summary>
    private static async Task AssertCancelsPromptly(Action<CancellationToken> work)
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
        Exception? error = null;
        try
        {
            run.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        TimeSpan latency = watch.Elapsed;

        await Assert.That(error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(latency < TimeSpan.FromMilliseconds(250) + (full / 2)).IsTrue().Because($"Stopped {latency.TotalMilliseconds:F0} ms after cancellation; the full run took {full.TotalMilliseconds:F0} ms.");
    }
}
