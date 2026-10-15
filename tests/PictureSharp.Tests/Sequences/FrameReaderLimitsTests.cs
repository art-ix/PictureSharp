using System.Buffers.Binary;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Sequences;

public class FrameReaderLimitsTests
{
    public static IEnumerable<string> Formats() => FrameWriterTests.Formats();

    private static async Task<byte[]> Encode(string format, ImageMetadata? metadata = null)
    {
        using Image source = FrameWriterTests.Pattern();
        using var data = new MemoryStream();
        await using var writer = Image.CreateFrameWriter(data, source.Size, FrameWriterTests.Options(format), new() { LoopCount = 3, Metadata = metadata });
        foreach (ImageFrame frame in source.Frames) await writer.WriteAsync(frame);
        await writer.CompleteAsync();
        return data.ToArray();
    }

    private static async Task ReadAll(byte[] bytes, DecoderOptions options)
    {
        using var input = new NonSeekableStream(bytes, 13);
        await using var reader = await Image.OpenFrameReaderAsync(input, options);
        while (await reader.MoveNextAsync()) { }
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task Limits_ApplyAcrossSequence_AndFirstFrameOnlyStillValidatesInput(string format)
    {
        byte[] data = await Encode(format, new ImageMetadata { Xmp = "<xmp>test limits</xmp>"u8.ToArray() });
        foreach (bool first in new[] { false, true })
        {
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new() { MaxInputBytes = data.Length - 1, FirstFrameOnly = first }));
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new() { MaxFrames = 4, FirstFrameOnly = first }));
            await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new() { MaxMetadataBytes = 1, FirstFrameOnly = first }));
        }
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new() { MaxPixels = 17 * 13 - 1 }));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new() { MaxPixels = 17 * 13 * 4 }));
        await ReadAll(data, new() { MaxPixels = 17 * 13 * 5, MaxFrames = 5, MaxInputBytes = data.Length });
        await ReadAll(data, new() { MaxMetadataBytes = 0, IgnoreMetadata = true });
        await ReadAll(data, new() { MaxMetadataBytes = long.MaxValue });
        using var firstInput = new NonSeekableStream(data, 17);
        await using var reader = await Image.OpenFrameReaderAsync(firstInput, new DecoderOptions { FirstFrameOnly = true, MaxPixels = 17 * 13 });
        using Image expected = Image.Load(data, new DecoderOptions { FirstFrameOnly = true });
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.GetPixelSpan().SequenceEqual(expected.GetPixelSpan())).IsTrue();
        await Assert.That(reader.Current.Duration).IsEqualTo(expected.Frames[0].Duration);
        await Assert.That(await reader.MoveNextAsync()).IsFalse();
        await Assert.That(reader.LoopCount).IsEqualTo(expected.LoopCount);
        await Sequence.AssertEqual(expected.Metadata.Xmp, reader.Metadata.Xmp);
    }

    [Test]
    [MethodDataSource(nameof(Formats))]
    public async Task BorrowedFrame_CloneAndEarlyExit_AndCancellation(string format)
    {
        byte[] data = await Encode(format);
        using var input = new NonSeekableStream(data, 3);
        var reader = await Image.OpenFrameReaderAsync(input);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        using var image = new Image(17, 13);
        Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Add(reader.Current));
        ImageFrame snapshot = reader.Current.Clone();
        image.Frames.Add(snapshot);
        await foreach (ImageFrame frame in reader.ReadFramesAsync()) break;
        await Assert.That(reader.FramesRead).IsEqualTo(2);
        await Assert.That(input.CanRead).IsTrue();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync());
        await using var canceled = await Image.OpenFrameReaderAsync(new NonSeekableStream(data));
        await canceled.MoveNextAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await canceled.MoveNextAsync(cts.Token));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await canceled.MoveNextAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Image.OpenFrameReaderAsync(new NonSeekableStream(data), cts.Token));
    }

    [Test]
    public async Task Apng_RejectsDeclaredCountMismatchAfterFirstFrame()
    {
        byte[] data = await Encode("png");
        int control = data.AsSpan().IndexOf("acTL"u8) + 4;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(control), 6);
        await Assert.ThrowsExactlyAsync<ImageFormatException>(() => ReadAll(data, new()));
    }

    [Test]
    [Property("Category", "Slow")]
    [MethodDataSource(nameof(Formats))]
    public async Task TruncationsAndMutations_DoNotLeakUncheckedExceptions(string format)
    {
        byte[] original = await Encode(format);
        var random = new Random(836);
        for (int i = 0; i < 120; i++)
        {
            byte[] data = i < 40 ? original[..(i * original.Length / 40)] : (byte[])original.Clone();
            if (i >= 40) data[random.Next(data.Length)] ^= (byte)(1 << random.Next(8));
            try { await ReadAll(data, new() { MaxPixels = 1_000_000, MaxInputBytes = 1_000_000, MaxMetadataBytes = 1_000_000, MaxFrames = 100 }); }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException) { }
        }
    }
}
