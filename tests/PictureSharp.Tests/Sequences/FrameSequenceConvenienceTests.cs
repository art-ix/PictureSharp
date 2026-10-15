using System.Drawing;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Sequences;

/// <summary>Extension-based writers, explicit durations, image frames and copied reader settings (0.6.1).</summary>
public class FrameSequenceConvenienceTests
{
    [Test]
    [Arguments("gif", "GIF")]
    [Arguments("png", "PNG")]
    [Arguments("apng", "PNG")]
    [Arguments("webp", "WebP")]
    public async Task CreateFrameWriter_ChoosesFormatFromExtension(string extension, string format)
    {
        using Image source = FrameWriterTests.Pattern();
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-extension-{Guid.NewGuid():N}.{extension}");
        try
        {
            await using (var writer = Image.CreateFrameWriter(path, source.Size, new ImageFrameWriterOptions { LoopCount = source.LoopCount }))
            {
                await Assert.That(writer.Format.Name).IsEqualTo(format);
                await Assert.That((writer.Width, writer.Height)).IsEqualTo((source.Width, source.Height));
                foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
                await writer.CompleteAsync();
            }

            using Image actual = Image.Load(path);
            await Assert.That(actual.SourceFormat!.Name).IsEqualTo(format);
            await Assert.That(actual.FrameCount).IsEqualTo(source.FrameCount);
            await Assert.That(actual.LoopCount).IsEqualTo(source.LoopCount);
        }
        finally { File.Delete(path); }
    }

    [Test]
    [Arguments("unknown.xyz")]
    [Arguments("still.jpg")]
    [Arguments("noextension")]
    public async Task CreateFrameWriter_UnsupportedExtension_ThrowsWithoutCreatingFile(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-extension-{Guid.NewGuid():N}-{name}");
        Assert.ThrowsExactly<NotSupportedException>(() => Image.CreateFrameWriter(path, new Size(2, 2)));
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    [MethodDataSource(typeof(FrameWriterTests), nameof(FrameWriterTests.Formats))]
    public async Task ExplicitDuration_MatchesFrameDuration_AndLeavesFrameUnchanged(string format)
    {
        using Image source = FrameWriterTests.Pattern();
        using var expected = new MemoryStream();
        await using (var writer = Image.CreateFrameWriter(expected, source.Size, FrameWriterTests.Options(format)))
        {
            foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
            await writer.CompleteAsync();
        }

        using var explicitFrames = new MemoryStream();
        using var images = new MemoryStream();
        await using (var frameWriter = Image.CreateFrameWriter(explicitFrames, source.Size, FrameWriterTests.Options(format)))
        await using (var imageWriter = Image.CreateFrameWriter(images, source.Size, FrameWriterTests.Options(format)))
        {
            foreach (ImageFrame frame in source.Frames)
            {
                TimeSpan duration = frame.Duration;
                ImageFrame copy = frame.Clone();
                copy.Duration = TimeSpan.FromSeconds(9);
                await frameWriter.WriteAsync(copy, duration);
                await Assert.That(copy.Duration).IsEqualTo(TimeSpan.FromSeconds(9));

                using var image = new Image(source.Width, source.Height);
                image.Frames.RootFrame.Duration = TimeSpan.FromSeconds(9);
                frame.GetPixelSpan().CopyTo(image.Frames.RootFrame.GetPixelSpan());
                await imageWriter.WriteAsync(image, duration);
                await Assert.That(image.Frames.RootFrame.Duration).IsEqualTo(TimeSpan.FromSeconds(9));
            }

            await frameWriter.CompleteAsync();
            await imageWriter.CompleteAsync();
        }

        await Sequence.AssertEqual(expected.ToArray(), explicitFrames.ToArray());
        await Sequence.AssertEqual(expected.ToArray(), images.ToArray());
    }

    [Test]
    public async Task WriteImage_RejectsInvalidArguments_AndLeavesImageUsable()
    {
        using var output = new MemoryStream();
        await using (var writer = Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Gif))
        {
            using var image = new Image(2, 2);
            Assert.ThrowsExactly<ArgumentNullException>(() => writer.WriteAsync((Image)null!, TimeSpan.Zero));
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(async () => await writer.WriteAsync(image, TimeSpan.FromMilliseconds(-1)));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await writer.WriteAsync(image, TimeSpan.Zero));
            await Assert.That(image.Width).IsEqualTo(2); // The caller's image stays usable.
        }

        await using (var writer = Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Gif))
        {
            using var wrongSize = new Image(3, 2);
            await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await writer.WriteAsync(wrongSize, TimeSpan.Zero));
        }

        await using (var writer = Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Gif))
        {
            var disposed = new Image(2, 2);
            disposed.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => writer.WriteAsync(disposed, TimeSpan.Zero));
        }
    }

    [Test]
    [MethodDataSource(typeof(FrameWriterTests), nameof(FrameWriterTests.Formats))]
    public async Task CloneCurrent_ReturnsIndependentStillImage(string format)
    {
        using Image source = FrameWriterTests.Pattern();
        byte[] data = source.Encode(FrameWriterTests.Options(format));
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(data));
        Assert.ThrowsExactly<InvalidOperationException>(() => reader.CloneCurrent());
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        using Image first = reader.CloneCurrent();
        await Assert.That(first.FrameCount).IsEqualTo(1);
        await Assert.That(first.SourceFormat).IsEqualTo(reader.Format);
        await Assert.That(first.Frames.RootFrame.Duration).IsEqualTo(source.Frames[0].Duration);
        await Assert.That(first.Frames.RootFrame.GetPixelSpan().SequenceEqual(source.Frames[0].GetPixelSpan())).IsTrue();

        first.Frames.RootFrame[0, 0] = new Rgba32(1, 2, 3);
        await Assert.That(reader.Current[0, 0]).IsNotEqualTo(new Rgba32(1, 2, 3));
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(first.Frames.RootFrame[0, 0]).IsEqualTo(new Rgba32(1, 2, 3));
    }

    [Test]
    public async Task StreamingResize_MatchesBatchResize()
    {
        using Image source = FrameWriterTests.Pattern();
        byte[] gif = source.Encode(ImageFormat.Gif);
        using var output = new MemoryStream();
        await using (ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(gif)))
        await using (ImageFrameWriter writer = Image.CreateFrameWriter(output, new Size(9, 7),
            new WebPEncoderOptions { Lossless = true, Exact = true }, ImageFrameWriterOptions.From(reader)))
        {
            await foreach (ImageFrame frame in reader.ReadFramesAsync())
            {
                using Image image = reader.CloneCurrent();
                image.Mutate(x => x.Resize(9, 7));
                await writer.WriteAsync(image, frame.Duration);
            }

            await writer.CompleteAsync();
        }

        using Image expected = Image.Load(gif);
        expected.Mutate(x => x.Resize(9, 7));
        using Image actual = Image.Load(output.ToArray());
        await FrameWriterTests.SameFrames(expected, actual);
    }

    [Test]
    public async Task From_CopiesReaderSettingsAsSnapshot()
    {
        using Image source = FrameWriterTests.Pattern();
        source.AnimationBackgroundColor = new Rgba32(10, 20, 30, 40);
        source.Metadata.IccProfile = [1, 2, 3];
        byte[] webp = source.Encode(new WebPEncoderOptions { Lossless = true });
        await using ImageFrameReader reader = await Image.OpenFrameReaderAsync(new MemoryStream(webp));

        ImageFrameWriterOptions options = ImageFrameWriterOptions.From(reader);
        await Assert.That(options.LoopCount).IsEqualTo(source.LoopCount);
        await Assert.That(options.AnimationBackgroundColor).IsEqualTo(reader.AnimationBackgroundColor);
        await Assert.That(options.FrameCount).IsNull();
        await Assert.That(options.UseTemporaryFile).IsFalse();
        await Sequence.AssertEqual(source.Metadata.IccProfile, options.Metadata!.IccProfile);
        await Assert.That(options.Metadata).IsNotSameReferenceAs(reader.Metadata);
        Assert.ThrowsExactly<ArgumentNullException>(() => ImageFrameWriterOptions.From(null!));

        using var gif = new MemoryStream();
        await using (var writer = Image.CreateFrameWriter(gif, reader.Size, ImageFormat.Gif, options))
        {
            await foreach (ImageFrame frame in reader.ReadFramesAsync()) await writer.WriteAsync(frame);
            await writer.CompleteAsync();
        }

        using Image actual = Image.Load(gif.ToArray());
        await Assert.That(actual.LoopCount).IsEqualTo(source.LoopCount);
    }
}
