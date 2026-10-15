using System.Drawing;
using PictureSharp.Formats.Gif;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Sequences;

public class FrameWriterTests
{
    public static IEnumerable<string> Formats() => ["gif", "png", "webp"];

    internal static EncoderOptions Options(string format) => format switch
    {
        "gif" => new GifEncoderOptions { Interlace = true },
        "png" => new PngEncoderOptions { Interlace = true },
        _ => new WebPEncoderOptions { Lossless = true, Exact = true, Method = 0 },
    };

    internal static Image Pattern(int count = 5)
    {
        var image = new Image(17, 13) { LoopCount = 3 };
        for (int i = 0; i < count; i++)
        {
            ImageFrame frame = i == 0 ? image.Frames[0] : new ImageFrame(17, 13);
            for (int y = 0; y < 13; y++)
                for (int x = 0; x < 17; x++)
                    frame[x, y] = ((x + i) % 7, (y + i) % 5) switch
                    {
                        (0, _) => default,
                        (_, 0) => new Rgba32(255, 0, 0),
                        _ => new Rgba32(0, 0, 255),
                    };
            frame.Duration = TimeSpan.FromMilliseconds(20 + i * 10);
            if (i != 0) image.Frames.Add(frame);
        }
        return image;
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Writer_RoundTripsSeekableAndTemporaryOutputs_AtNonzeroOffset(string format)
    {
        using Image source = Pattern(23); // Cross WebP's keyframe search/flush boundary.
        foreach (bool declared in new[] { false, true })
        foreach (bool seekable in new[] { false, true })
        {
            using Stream output = seekable ? new MemoryStream() : new NonSeekableWriteStream();
            if (seekable) output.Write(new byte[11]);
            await using var writer = Image.CreateFrameWriter(output, source.Size, Options(format), new()
            {
                FrameCount = declared ? source.FrameCount : null, LoopCount = source.LoopCount, UseTemporaryFile = true,
            });
            foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
            await Assert.That(writer.FramesWritten).IsEqualTo(source.FrameCount);
            await writer.CompleteAsync();
            await writer.CompleteAsync();
            await Assert.That(writer.IsComplete).IsTrue();
            await Assert.That(output.CanWrite).IsTrue();
            byte[] data = output is MemoryStream memory ? memory.ToArray()[11..] : ((NonSeekableWriteStream)output).ToArray();
            using Image decoded = Image.Load(data, new DecoderOptions { Png = new() { StrictChecksums = true } });
            await SameFrames(source, decoded);
            if (format == "webp") await Assert.That(data.SequenceEqual(source.Encode(Options(format)))).IsTrue();
        }
    }

    [Test]
    public async Task Conversion_AllNineFormatPairs_PreservesFramesWithoutCollectingThem()
    {
        using Image source = Pattern();
        foreach (string from in Formats())
        foreach (string to in Formats())
        {
            using var encoded = new MemoryStream();
            await using (var writer = Image.CreateFrameWriter(encoded, source.Size, Options(from)))
            {
                foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
                await writer.CompleteAsync();
            }
            using var input = new NonSeekableStream(encoded.ToArray(), 7);
            await using var reader = await Image.OpenFrameReaderAsync(input);
            using var output = new NonSeekableWriteStream();
            await using (var writer = Image.CreateFrameWriter(output, reader.Size, Options(to), new() { UseTemporaryFile = true }))
            {
                await foreach (ImageFrame frame in reader.ReadFramesAsync()) await writer.WriteAsync(frame);
                await writer.CompleteAsync();
            }
            using Image converted = Image.Load(output.ToArray());
            await SameFrames(source, converted, compareLoop: false);
        }
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task DeclaredSingleFrame_UsesNormalEncoder_EvenOnNonSeekableOutput(string format)
    {
        using Image source = Pattern(1);
        source.Metadata.Xmp = "<xmp>test</xmp>"u8.ToArray();
        using var output = new NonSeekableWriteStream();
        await using var writer = Image.CreateFrameWriter(output, source.Size, Options(format), new() { FrameCount = 1, Metadata = source.Metadata });
        await writer.WriteAsync(source.Frames[0]);
        await writer.CompleteAsync();
        await Assert.That(output.ToArray().SequenceEqual(source.Encode(Options(format)))).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Metadata_IsSnapshottedAndFiltered(string format)
    {
        using Image source = Pattern();
        var metadata = new ImageMetadata { IccProfile = [1, 2, 3], Xmp = "<xmp>sequence</xmp>"u8.ToArray(), HorizontalResolution = 144, VerticalResolution = 72 };
        foreach (MetadataKinds kinds in new[] { MetadataKinds.All, MetadataKinds.None })
        {
            using var output = new MemoryStream();
            ImageMetadata copy = metadata.Clone();
            await using var writer = Image.CreateFrameWriter(output, source.Size, Options(format) with { Metadata = kinds }, new() { Metadata = copy });
            copy.Clear();
            foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
            await writer.CompleteAsync();
            using Image actual = Image.Load(output.ToArray());
            await Sequence.AssertEqual(kinds == MetadataKinds.All ? metadata.IccProfile : null, actual.Metadata.IccProfile);
            await Sequence.AssertEqual(kinds == MetadataKinds.All ? metadata.Xmp : null, actual.Metadata.Xmp);
            if (format == "png") await Assert.That(actual.Metadata.HorizontalResolution).IsEqualTo(144f).Within(0.02f);
        }
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task CountsCancellationAndAbandonment_DeleteIncompletePaths(string format)
    {
        using Image source = Pattern();
        string path = Path.Combine(Path.GetTempPath(), $"picturesharp-sequence-{Guid.NewGuid():N}.{format}");
        try
        {
            foreach (string failure in new[] { "empty", "short", "extra", "canvas", "cancel", "finish-cancel", "dispose" })
            {
                await using var writer = Image.CreateFrameWriter(path, source.Size, Options(format), new() { FrameCount = 2 });
                if (failure != "empty") await writer.WriteAsync(source.Frames[0]);
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                switch (failure)
                {
                    case "empty": case "short":
                        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await writer.CompleteAsync()); break;
                    case "extra":
                        await writer.WriteAsync(source.Frames[1]);
                        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await writer.WriteAsync(source.Frames[2])); break;
                    case "canvas":
                        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await writer.WriteAsync(new ImageFrame(1, 1))); break;
                    case "cancel":
                        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.WriteAsync(source.Frames[1], cts.Token)); break;
                    case "finish-cancel":
                        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.CompleteAsync(cts.Token)); break;
                    default: await writer.DisposeAsync(); break;
                }
                await Assert.That(File.Exists(path)).IsFalse().Because(failure);
                await Assert.That(writer.IsComplete).IsFalse();
            }
            await using var completed = Image.CreateFrameWriter(path, source.Size, Options(format), new() { FrameCount = 1 });
            await completed.WriteAsync(source.Frames[0]);
            await completed.CompleteAsync();
            using Image read = Image.Load(path); // Owned file must already be closed.
            await Assert.That(read.Size).IsEqualTo(source.Size);
        }
        finally { File.Delete(path); }
    }

    [Test]
    public async Task CapabilityErrors_DoNotTouchOutput_AndKnownCountApngWritesForward()
    {
        using var output = new NonSeekableWriteStream();
        Assert.ThrowsExactly<NotSupportedException>(() => Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.WebP));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Png));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Jpeg));
        Assert.ThrowsExactly<NotSupportedException>(() => Image.CreateFrameWriter(output, new Size(2, 2), new PngEncoderOptions { ColorType = PngColorType.Palette }));
        await Assert.That(output.ToArray().Length).IsEqualTo(0);
        await using var png = Image.CreateFrameWriter(output, new Size(2, 2), ImageFormat.Png, new() { FrameCount = 2 });
        await png.WriteAsync(new ImageFrame(2, 2));
        await png.WriteAsync(new ImageFrame(2, 2));
        await png.CompleteAsync();
        using Image actual = Image.Load(output.ToArray());
        await Assert.That(actual.FrameCount).IsEqualTo(2);
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task IoFailureAndCancellationDuringWrite_AbortAndLeaveCallerStreamOpen(string format)
    {
        using Image source = Pattern();
        using var cts = new CancellationTokenSource();
        foreach (bool cancel in new[] { false, true })
        {
            using var output = new FailingStream(cancel ? cts.Cancel : null);
            await using var writer = Image.CreateFrameWriter(output, source.Size, Options(format), new() { FrameCount = 2 });
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.WriteAsync(source.Frames[0], cts.Token));
            else await Assert.ThrowsExactlyAsync<IOException>(async () => await writer.WriteAsync(source.Frames[0]));
            await Assert.That(output.CanWrite).IsTrue();
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await writer.WriteAsync(source.Frames[0]));
        }
    }

    private sealed class FailingStream(Action? cancel) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancel is null) throw new IOException("test output failure");
            cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    internal static async Task SameFrames(Image expected, Image actual, bool compareLoop = true)
    {
        await Assert.That(actual.FrameCount).IsEqualTo(expected.FrameCount);
        if (compareLoop) await Assert.That(actual.LoopCount).IsEqualTo(expected.LoopCount);
        for (int i = 0; i < expected.FrameCount; i++)
        {
            await Assert.That(actual.Frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
            await Assert.That(actual.Frames[i].GetPixelSpan().SequenceEqual(expected.Frames[i].GetPixelSpan())).IsTrue().Because($"frame {i}");
        }
    }
}
