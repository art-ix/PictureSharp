using System.Drawing;
using RasterSharp.Tests.Infrastructure;

namespace RasterSharp.Tests.Core;

/// <summary>The code samples from README.md, kept compiling and running against corpus files.</summary>
public sealed class ReadmeExamplesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rastersharp-readme-{Guid.NewGuid():N}");

    public ReadmeExamplesTests()
    {
        Directory.CreateDirectory(_dir);
        File.Copy(Corpus.PathOf("jpeg/q90_444.jpg"), Path.Combine(_dir, "photo.jpg"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), Path.Combine(_dir, "input.png"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), Path.Combine(_dir, "logo.png"));
        File.Copy(Corpus.PathOf("png/odd_rgba.png"), Path.Combine(_dir, "frame-1.png"));
        using (Image second = Image.Load(Path.Combine(_dir, "frame-1.png")))
        {
            second.Mutate(x => x.Invert());
            second.Save(Path.Combine(_dir, "frame-2.png"));
        }

        File.WriteAllBytes(Path.Combine(_dir, "unknown.bin"), File.ReadAllBytes(Corpus.PathOf("png/odd_rgba.png")));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task QuickStart()
    {
        using var image = await Image.LoadAsync(P("photo.jpg"));
        image.Mutate(x => x
            .AutoOrient()
            .Resize(1600, 0)                 // 0 keeps the aspect ratio
            .UnsharpMask());
        await image.SaveAsWebPAsync(P("photo.webp"), new() { Quality = 82 });

        using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
        thumbnail.SaveAsPng(P("thumbnail.png"));

        Assert.Equal(1600, Image.Identify(P("photo.webp")).Width);
        Assert.Equal((256, 256), (Image.Identify(P("thumbnail.png")).Width, Image.Identify(P("thumbnail.png")).Height));
    }

    [Fact]
    public void ReadingAndWriting()
    {
        using var stream = new MemoryStream();
        using var image = Image.Load(P("input.png"), new DecoderOptions
        {
            ApplyExifOrientation = true,
            MaxPixels = 40_000_000,
        });

        image.Save(P("output.webp"));
        image.SaveAsWebP(P("exact.webp"), new() { Lossless = true, Exact = true });
        image.SaveAsPng(P("output.png"), new() { Interlace = true });
        image.SaveAsJpeg(P("output.jpg"), new() { Quality = 90, Progressive = true });
        image.SaveAsBmp(stream);
        byte[] webp = image.ToWebP();

        ImageInfo info = Image.Identify(P("output.webp"));
        Assert.Equal($"WebP: {image.Width}x{image.Height}, frames: 1", $"{info.Format.Name}: {info.Width}x{info.Height}, frames: {info.FrameCount}");
        ImageFormat? format = Image.DetectFormat(P("unknown.bin"));
        Assert.Equal(ImageFormat.Png, format);
        Assert.Equal(ImageFormat.Bmp, Image.DetectFormat(stream.ToArray()));
        Assert.Equal(ImageFormat.WebP, Image.DetectFormat(webp));
    }

    [Fact]
    public void AnimatedWebP()
    {
        using var first = Image.Load(P("frame-1.png"));
        using var second = Image.Load(P("frame-2.png"));
        using var animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(100), first, second);
        animation.LoopCount = 0;                                   // forever
        animation.Frames[1].Duration = TimeSpan.FromMilliseconds(150);
        animation.SaveAsWebP(P("animation.webp"), new() { Lossless = true, Exact = true, MinimizeSize = true });

        using Image back = Image.Load(P("animation.webp"));
        Assert.Equal(2, back.FrameCount);
        Assert.Equal(first.GetPixelSpan().ToArray(), back.GetPixelSpan().ToArray());
    }

    [Fact]
    public void ImageProcessing()
    {
        using var photo = Image.Load(P("photo.jpg"));
        using var logo = Image.Load(P("logo.png"));
        photo.Mutate(x => x
            .AutoOrient()
            .Resize(new ResizeOptions { Size = new(1200, 800), Mode = ResizeMode.Crop, Position = AnchorPosition.Top })
            .Contrast(1.1f)
            .DrawImage(logo, AnchorPosition.BottomRight, opacity: 0.8f));
        photo.SaveAsJpeg(P("card.jpg"), new() { Quality = 88 });

        Assert.Equal(new Size(1200, 800), photo.Size);
        using Image framed = photo.Clone(x => x.Polaroid());
        Assert.Equal(new Size(1240, 920), framed.Size);
    }

    [Fact]
    public void PixelsAndFrames()
    {
        using var image = new Image(64, 32, Color.CornflowerBlue);
        image[0, 0] = Rgba32.Parse("#ff8800");                 // Color and Rgba32 convert implicitly
        Span<Rgba32> row = image.GetRowSpan(1);
        row.Fill(new Rgba32(255, 255, 255, 128));

        byte[] bgra = image.ToPixelData(PixelFormat.Bgra32);   // e.g. WPF BitmapSource.Create or SkiaSharp Bgra8888
        using Image copy = Image.LoadPixelData(bgra, image.Width, image.Height, PixelFormat.Bgra32);

        Assert.Equal(image.GetPixelSpan().ToArray(), copy.GetPixelSpan().ToArray());
        Assert.Equal(new Rgba32(255, 136, 0), copy[0, 0]);
    }
}

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Polaroid() => x.Pad(x.Size.Width + 40, x.Size.Height + 120, Color.White, AnchorPosition.Top);
    }
}
