using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.IO;
using PictureSharp.IO;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

public class SequenceContractTests
{
    [Fact]
    public async Task Reader_BorrowsFrames_AndEarlyBreakDisposesCodecAndOwnedStream()
    {
        var input = new MemoryStream();
        var reader = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), false,
            [new(2, 2), new(2, 2)]);
        using var image = new Image(2, 2);
        ImageFrame? retained = null;
        await foreach (ImageFrame frame in reader.ReadFrames())
        {
            Assert.Throws<ArgumentException>(() => image.Frames.Add(frame));
            retained = frame.Clone();
            break;
        }
        Assert.Equal(1, reader.Reads);
        Assert.Equal(1, reader.Disposals);
        Assert.False(input.CanRead);
        image.Frames.Add(retained!);
        Assert.Equal(2, image.FrameCount);
        await reader.DisposeAsync();
        Assert.Equal(1, reader.Disposals);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task Reader_RejectsEmptyMissingAndExtraFrames_AndCleansUp(int actual, int? declared)
    {
        using var input = new MemoryStream();
        var frames = Enumerable.Range(0, actual).Select(_ => new ImageFrame(2, 2)).ToArray();
        var reader = new Reader(input, new(ImageFormat.Gif, 2, 2, declared), new(DecoderOptions.Default), true, frames);
        await Assert.ThrowsAsync<ImageFormatException>(async () =>
        {
            while (await reader.MoveNextAsync()) { }
        });
        Assert.Equal(1, reader.Disposals);
        Assert.True(input.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Reader_HonorsSharedBudgetsAndCanvas_AndRejectsConcurrentOperations()
    {
        using var input = new MemoryStream();
        var context = new DecodeContext(new DecoderOptions { MaxPixels = 7 });
        var reader = new Reader(input, new(ImageFormat.Gif, 2, 2), context, true, [new(2, 2), new(2, 2)]);
        Assert.True(await reader.MoveNextAsync());
        await Assert.ThrowsAsync<ImageFormatException>(async () => await reader.MoveNextAsync());
        Assert.Equal(4, context.Budget.Pixels);
        var bad = new Reader(input, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true, [new(1, 1)]);
        await Assert.ThrowsAsync<ImageFormatException>(async () => await bad.MoveNextAsync());
        var pending = new Reader(input, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true, [])
        {
            Pending = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        Task<bool> first = pending.MoveNextAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending.MoveNextAsync());
        pending.Pending.SetResult(new ImageFrame(2, 2));
        Assert.True(await first);
        await pending.DisposeAsync();
    }

    [Fact]
    public async Task Reader_CancellationAndIoFailure_ArePreservedAndDisposeState()
    {
        using var input = new MemoryStream();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var reader = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), true, []);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync(cts.Token));
        Assert.Equal(0, reader.Reads);
        Assert.Equal(1, reader.Disposals);
        var io = new IOException("input failed");
        var broken = new Reader(input, new(ImageFormat.Png, 2, 2), new(DecoderOptions.Default), true, []) { Failure = io };
        Assert.Same(io, await Assert.ThrowsAsync<IOException>(async () => await broken.MoveNextAsync()));
        Assert.Equal(1, broken.Disposals);
    }

    [Fact]
    public async Task Writer_BorrowsFrames_CompletesOnce_AndDoesNotCompleteOnDispose()
    {
        using var output = new MemoryStream();
        using var image = new Image(2, 2);
        var writer = new Writer(output, new(ImageFormat.Gif, 2, 2, 1), new(DecoderOptions.Default), true);
        await writer.WriteAsync(image.Frames[0]);
        Assert.True(image.Frames[0].IsOwned);
        await writer.CompleteAsync();
        await writer.CompleteAsync();
        Assert.Equal(1, writer.Completions);
        await writer.DisposeAsync();
        Assert.True(output.CanWrite);
        var unfinished = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        await unfinished.WriteAsync(image.Frames[0]);
        await unfinished.DisposeAsync();
        Assert.Equal(0, unfinished.Completions);
    }

    [Fact]
    public async Task Writer_RejectsCountsCanvasAndLimits_AndCleansUpAfterIoFailure()
    {
        using var output = new MemoryStream();
        var incomplete = new Writer(output, new(ImageFormat.Gif, 2, 2, 2), new(DecoderOptions.Default), true);
        await incomplete.WriteAsync(new(2, 2));
        await Assert.ThrowsAsync<ImageFormatException>(async () => await incomplete.CompleteAsync());
        Assert.Equal(0, incomplete.Completions);
        var extra = new Writer(output, new(ImageFormat.Gif, 2, 2, 1), new(DecoderOptions.Default), true);
        await extra.WriteAsync(new(2, 2));
        await Assert.ThrowsAsync<ImageFormatException>(async () => await extra.WriteAsync(new(2, 2)));
        var canvas = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        await Assert.ThrowsAsync<ArgumentException>(async () => await canvas.WriteAsync(new(1, 1)));
        var limited = new Writer(output, new(ImageFormat.Gif, 2, 2), new(new DecoderOptions { MaxPixels = 3 }), true);
        await Assert.ThrowsAsync<ImageFormatException>(async () => await limited.WriteAsync(new(2, 2)));
        Assert.Equal(0, limited.Writes);
        var io = new IOException("output failed");
        var failing = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true) { Failure = io };
        Assert.Same(io, await Assert.ThrowsAsync<IOException>(async () => await failing.WriteAsync(new(2, 2))));
        Assert.Equal(1, failing.Disposals);
    }

    [Fact]
    public void Factory_RejectsUnsupportedCapabilitiesBeforeOpeningOrReading()
    {
        var codec = new SequentialCodec();
        using var input = new NonSeekableStream([1, 2]);
        var context = new DecodeContext(DecoderOptions.Default);
        Assert.Throws<NotSupportedException>(() => SequenceSessionFactory.OpenReader(codec, input, context));
        Assert.Equal(0, input.BytesRead);
        using var output = new MemoryStream();
        Assert.Throws<NotSupportedException>(() => SequenceSessionFactory.OpenWriter(codec, output, new(ImageFormat.Png, 2, 2), context));
        Assert.Equal(0, codec.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BudgetedInput_StopsAfterLimitPlusProbe_DespiteShortReadsOrFalseLength(bool async)
    {
        using var input = new DeclaredLengthStream(new byte[20], 1);
        var context = new DecodeContext(new DecoderOptions { MaxInputBytes = 7 });
        using var bounded = new BudgetedReadStream(input, context, true);
        var buffer = new byte[30];
        if (async) await Assert.ThrowsAsync<ImageFormatException>(async () => Assert.InRange(await bounded.ReadAsync(buffer), 0, buffer.Length));
        else Assert.Throws<ImageFormatException>(() => bounded.Read(buffer));
        Assert.Equal(8, input.BytesRead);
        Assert.False(bounded.CanSeek);
        using var shortInput = new NonSeekableStream(new byte[7], 1);
        using var exact = new BudgetedReadStream(shortInput, new(new DecoderOptions { MaxInputBytes = 7 }), true);
        for (int i = 0; i < 7; i++) Assert.Equal(1, await exact.ReadAsync(buffer));
        Assert.Equal(0, await exact.ReadAsync(buffer));
        bounded.Dispose();
        Assert.True(input.CanRead);
        Assert.Throws<ObjectDisposedException>(() => bounded.Read(buffer));
    }

    [Fact]
    public async Task Writer_CancellationBeforeWriteOrAfterFooter_CleansUpAndPreservesOwnership()
    {
        using var output = new MemoryStream();
        using var image = new Image(2, 2);
        using var cts = new CancellationTokenSource();
        var writer = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default), true);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.WriteAsync(image.Frames[0], cts.Token));
        Assert.Equal(0, writer.Writes);
        Assert.True(image.Frames[0].IsOwned);
        Assert.Equal(1, writer.Disposals);
        using var finish = new CancellationTokenSource();
        var footer = new Writer(output, new(ImageFormat.Gif, 2, 2), new(DecoderOptions.Default, finish.Token), true) { AfterComplete = finish.Cancel };
        await footer.WriteAsync(image.Frames[0]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await footer.CompleteAsync());
        Assert.Equal(1, footer.Disposals);
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
