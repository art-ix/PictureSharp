using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class SequenceContractTests
{
    [Test]
    public async Task Reader_BorrowsFrames_AndEarlyBreakDisposesCodecAndOwnedStream()
    {
        var input = new MemoryStream();
        var reader = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), false,
            [new(2, 2), new(2, 2)]);
        using var image = new Image(2, 2);
        ImageFrame? retained = null;
        await foreach (ImageFrame frame in reader.ReadFrames())
        {
            Assert.ThrowsExactly<ArgumentException>(() => image.Frames.Add(frame));
            retained = frame.Clone();
            break;
        }
        await Assert.That(reader.Reads).IsEqualTo(1);
        await Assert.That(reader.Disposals).IsEqualTo(1);
        await Assert.That(input.CanRead).IsFalse();
        image.Frames.Add(retained!);
        await Assert.That(image.FrameCount).IsEqualTo(2);
        await reader.DisposeAsync();
        await Assert.That(reader.Disposals).IsEqualTo(1);
    }

    [Test]
    [Arguments(0, null)]
    [Arguments(1, 2)]
    [Arguments(2, 1)]
    public async Task Reader_RejectsEmptyMissingAndExtraFrames_AndCleansUp(int actual, int? declared)
    {
        using var input = new MemoryStream();
        var frames = Enumerable.Range(0, actual).Select(_ => new ImageFrame(2, 2)).ToArray();
        var reader = new Reader(input, new(ImageFormat.Gif, 2, 2, declared), new(DecoderOptions.Default), true, frames);
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () =>
        {
            while (await reader.MoveNextAsync()) { }
        });
        await Assert.That(reader.Disposals).IsEqualTo(1);
        await Assert.That(input.CanRead).IsTrue();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync());
    }

    [Test]
    public async Task Reader_HonorsSharedBudgetsAndCanvas_AndRejectsConcurrentOperations()
    {
        using var input = new MemoryStream();
        var context = new DecodeContext(new DecoderOptions { MaxPixels = 7 });
        var reader = new Reader(input, new(ImageFormat.Gif, 2, 2), context, true, [new(2, 2), new(2, 2)]);
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await reader.MoveNextAsync());
        await Assert.That(context.Budget.Pixels).IsEqualTo(4);
        var bad = new Reader(input, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true, [new(1, 1)]);
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await bad.MoveNextAsync());
        var pending = new Reader(input, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true, [])
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        Task<bool> first = pending.MoveNextAsync().AsTask();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await pending.MoveNextAsync());
        pending.Pending.SetResult(new ImageFrame(2, 2));
        await Assert.That(await first).IsTrue();
        await pending.DisposeAsync();
    }

    [Test]
    public async Task Reader_CancellationAndIoFailure_ArePreservedAndDisposeState()
    {
        using var input = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), true, []);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await reader.MoveNextAsync(cts.Token));
        await Assert.That(reader.Reads).IsEqualTo(0);
        await Assert.That(reader.Disposals).IsEqualTo(1);
        var io = new IOException("input failed");
        var broken = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), true, []) { Failure = io };
        await Assert.That(await Assert.ThrowsExactlyAsync<IOException>(async () => await broken.MoveNextAsync())).IsSameReferenceAs(io);
        await Assert.That(broken.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Writer_BorrowsFrames_CompletesOnce_AndDoesNotCompleteOnDispose()
    {
        using var output = new MemoryStream();
        using var image = new Image(2, 2);
        var writer = new Writer(output, new(ImageFormat.Gif, 2, 2, 1), new(DecoderOptions.Default), true);
        await writer.WriteAsync(image.Frames[0]);
        await Assert.That(image.Frames[0].IsOwned).IsTrue();
        await writer.CompleteAsync();
        await writer.CompleteAsync();
        await Assert.That(writer.Completions).IsEqualTo(1);
        await writer.DisposeAsync();
        await Assert.That(output.CanWrite).IsTrue();
        var unfinished = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        await unfinished.WriteAsync(image.Frames[0]);
        await unfinished.DisposeAsync();
        await Assert.That(unfinished.Completions).IsEqualTo(0);
    }

    [Test]
    public async Task Writer_RejectsCountsCanvasAndLimits_AndCleansUpAfterIoFailure()
    {
        using var output = new MemoryStream();
        var incomplete = new Writer(output, new(ImageFormat.Gif, 2, 2, 2), new(DecoderOptions.Default), true);
        await incomplete.WriteAsync(new(2, 2));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await incomplete.CompleteAsync());
        await Assert.That(incomplete.Completions).IsEqualTo(0);
        var extra = new Writer(output, new(ImageFormat.Gif, 2, 2, 1), new(DecoderOptions.Default), true);
        await extra.WriteAsync(new(2, 2));
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await extra.WriteAsync(new(2, 2)));
        var canvas = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await canvas.WriteAsync(new(1, 1)));
        var limited = new Writer(output, new(ImageFormat.Gif, 2, 2), new(new DecoderOptions { MaxPixels = 3 }), true);
        await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => await limited.WriteAsync(new(2, 2)));
        await Assert.That(limited.Writes).IsEqualTo(0);
        var io = new IOException("output failed");
        var failing = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true) { Failure = io };
        await Assert.That(await Assert.ThrowsExactlyAsync<IOException>(async () => await failing.WriteAsync(new(2, 2)))).IsSameReferenceAs(io);
        await Assert.That(failing.Disposals).IsEqualTo(1);
    }

    [Test]
    public async Task Factory_RejectsUnsupportedCapabilitiesBeforeOpeningOrReading()
    {
        var codec = new SequentialCodec();
        using var input = new NonSeekableStream([1, 2]);
        var context = new DecodeContext(DecoderOptions.Default);
        Assert.ThrowsExactly<NotSupportedException>(() => SequenceSessionFactory.OpenReader(codec, input, context));
        await Assert.That(input.BytesRead).IsEqualTo(0);
        using var output = new MemoryStream();
        Assert.ThrowsExactly<NotSupportedException>(() => SequenceSessionFactory.OpenWriter(codec, output, new(ImageFormat.Png, 2, 2), context));
        await Assert.That(codec.Opens).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BudgetedInput_StopsAfterLimitPlusProbe_DespiteShortReadsOrFalseLength(bool async)
    {
        using var input = new DeclaredLengthStream(new byte[20], 1);
        var context = new DecodeContext(new DecoderOptions { MaxInputBytes = 7 });
        using var bounded = new BudgetedReadStream(input, context, true);
        var buffer = new byte[30];
        if (async) await Assert.ThrowsExactlyAsync<ImageFormatException>(async () => _ = await bounded.ReadAsync(buffer));
        else Assert.ThrowsExactly<ImageFormatException>(() => _ = bounded.Read(buffer));
        await Assert.That(input.BytesRead).IsEqualTo(8);
        await Assert.That(bounded.CanSeek).IsFalse();
        using var shortInput = new NonSeekableStream(new byte[7], 1);
        using var exact = new BudgetedReadStream(shortInput, new(new DecoderOptions { MaxInputBytes = 7 }), true);
        for (int i = 0; i < 7; i++) await Assert.That(await exact.ReadAsync(buffer)).IsEqualTo(1);
        await Assert.That(await exact.ReadAsync(buffer)).IsEqualTo(0);
        bounded.Dispose();
        await Assert.That(input.CanRead).IsTrue();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = bounded.Read(buffer));
    }

    [Test]
    public async Task Writer_CancellationBeforeWriteOrAfterFooter_CleansUpAndPreservesOwnership()
    {
        using var output = new MemoryStream();
        using var image = new Image(2, 2);
        using var cts = new CancellationTokenSource();
        var writer = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.WriteAsync(image.Frames[0], cts.Token));
        await Assert.That(writer.Writes).IsEqualTo(0);
        await Assert.That(image.Frames[0].IsOwned).IsTrue();
        await Assert.That(writer.Disposals).IsEqualTo(1);
        using var finish = new CancellationTokenSource();
        var footer = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default, finish.Token), true) { AfterComplete = finish.Cancel };
        await footer.WriteAsync(image.Frames[0]);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await footer.CompleteAsync());
        await Assert.That(footer.Disposals).IsEqualTo(1);
    }

    private sealed class Reader(Stream input, FrameSequenceInfo info, DecodeContext context, bool leaveOpen, ImageFrame[] frames)
        : FrameReadSession(input, info, context, leaveOpen)
    {
        public int Reads;
        public int Disposals;
        public Exception? Failure;
        public TaskCompletionSource<ImageFrame?>? Pending;
        protected override ValueTask<ImageFrame?> ReadNextCoreAsync(CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            int index = Reads++;
            return Pending is not null ? new(Pending.Task) : ValueTask.FromResult(index < frames.Length ? frames[index] : null);
        }
        protected override void DisposeReader() => Disposals++;
    }

    private sealed class Writer(Stream output, FrameSequenceInfo info, DecodeContext context, bool leaveOpen)
        : FrameWriteSession(output, info, context, leaveOpen)
    {
        public int Writes;
        public int Completions;
        public Action? AfterComplete;
        public int Disposals;
        public Exception? Failure;
        protected override ValueTask WriteCoreAsync(ImageFrame frame, CancellationToken cancellationToken)
        {
            if (Failure is not null) throw Failure;
            Writes++;
            return ValueTask.CompletedTask;
        }
        protected override ValueTask CompleteCoreAsync(CancellationToken cancellationToken) { Completions++; AfterComplete?.Invoke(); return ValueTask.CompletedTask; }
        protected override void DisposeState() => Disposals++;
    }

    private sealed class SequentialCodec : ISequentialImageDecoder, ISequentialImageEncoder
    {
        public int Opens;
        public bool SupportsNonSeekableInput => false;
        public bool SupportsNonSeekableOutput => false;
        public bool RequiresFrameCount => true;
        public FrameReadSession Open(Stream input, DecodeContext context, bool leaveOpen)
        { Opens++; return new Reader(input, new(ImageFormat.Png, 2, 2), context, leaveOpen, []); }
        public FrameWriteSession Open(Stream output, FrameSequenceInfo info, DecodeContext context, bool leaveOpen)
        { Opens++; return new Writer(output, info, context, leaveOpen); }
    }
}
