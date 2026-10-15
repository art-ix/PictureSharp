using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp.Tests.Extensibility;

/// <summary>Swaps rows and columns; a struct, so ApplyProcessor runs it without boxing.</summary>
public readonly struct TransposeProcessor : IFrameProcessor
{
    public Size GetOutputSize(Size source) => new(source.Height, source.Width);

    public void Process(ReadOnlySpan<Rgba32> source, Size sourceSize, Span<Rgba32> destination, Size destinationSize, CancellationToken cancellationToken)
    {
        for (int y = 0; y < sourceSize.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < sourceSize.Width; x++)
            {
                destination[x * destinationSize.Width + y] = source[y * sourceSize.Width + x];
            }
        }
    }
}

/// <summary>A class processor with configurable behavior for failure and contract tests.</summary>
public sealed class ScriptedProcessor(Func<Size, Size> size, Action<int>? onFrame = null) : IFrameProcessor
{
    private int _frame;

    public Size GetOutputSize(Size source) => size(source);

    public void Process(ReadOnlySpan<Rgba32> source, Size sourceSize, Span<Rgba32> destination, Size destinationSize, CancellationToken cancellationToken)
    {
        onFrame?.Invoke(_frame++);
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>The pattern for a named custom operation that chains like the built-in ones.</summary>
public static class TransposeExtensions
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext Transpose() => x.ApplyProcessor(new TransposeProcessor());
    }
}

public class FrameProcessorTests
{
    private static Image Numbered(int width, int height, byte offset = 0)
    {
        var image = new Image(width, height);
        Span<Rgba32> pixels = image.GetPixelSpan();
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = new Rgba32((byte)(offset + i), (byte)i, 0);
        }

        return image;
    }

    [Test]
    public async Task StructProcessor_ChangesCanvasSize_AndWritesExpectedPixels()
    {
        using Image image = Numbered(3, 2);
        image.Mutate(x => x.Transpose());
        await Assert.That(image.Size).IsEqualTo(new Size(2, 3));
        // Source rows (0 1 2) and (3 4 5) become columns.
        await Assert.That(image.GetPixelSpan().ToArray().Select(p => (int)p.R)).IsEquivalentTo([0, 3, 1, 4, 2, 5], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Processor_ChainsWithBuiltInOperations_AndRunsOnEveryFrame()
    {
        using Image first = Numbered(4, 2);
        using Image second = Numbered(4, 2, offset: 100);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), first, second);
        animation.Mutate(x => x.Transpose().Flip(FlipMode.Horizontal).Transpose());

        await Assert.That(animation.Size).IsEqualTo(new Size(4, 2));
        foreach (var frame in animation.Frames)
        {
            await Assert.That(frame.Duration).IsEqualTo(TimeSpan.FromMilliseconds(70));
        }

        // Transpose, horizontal flip and transpose again amount to a vertical flip.
        await Assert.That(animation.Frames[0].GetPixelSpan().ToArray().Select(p => (int)p.R)).IsEquivalentTo([4, 5, 6, 7, 0, 1, 2, 3], CollectionOrdering.Matching);
        await Assert.That((int)(animation.Frames[1].GetPixelSpan()[0].R)).IsEqualTo(104);
    }

    [Test]
    public async Task Destination_StartsTransparentBlack()
    {
        using var image = new Image(2, 2, new Rgba32(9, 9, 9));
        image.Mutate(x => x.ApplyProcessor(new ScriptedProcessor(_ => new Size(3, 1))));
        await Assert.That(image.Size).IsEqualTo(new Size(3, 1));
        foreach (var p in image.GetPixelSpan().ToArray())
        {
            await Assert.That(p).IsEqualTo(default);
        }
    }

    [Test]
    public async Task FailureOrCancellation_OnALaterFrame_KeepsEveryFrame()
    {
        using Image first = Numbered(4, 2);
        using Image second = Numbered(4, 2, offset: 50);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), first, second);
        Rgba32[][] before = [.. animation.Frames.Select(f => f.GetPixelSpan().ToArray())];

        var failing = new ScriptedProcessor(s => new Size(1, 1), frame => { if (frame == 1) throw new InvalidDataException("boom"); });
        Assert.ThrowsExactly<InvalidDataException>(() => animation.Mutate(x => x.ApplyProcessor(failing)));
        await AssertUnchanged();

        using var cts = new CancellationTokenSource();
        var canceling = new ScriptedProcessor(s => new Size(1, 1), frame => { if (frame == 1) cts.Cancel(); });
        Assert.Throws<OperationCanceledException>(() => animation.Mutate(x => x.ApplyProcessor(canceling), cts.Token));
        await AssertUnchanged();

        async Task AssertUnchanged()
        {
            await Assert.That(animation.Size).IsEqualTo(new Size(4, 2));
            for (int i = 0; i < before.Length; i++)
            {
                await Assert.That(animation.Frames[i].GetPixelSpan().ToArray()).IsEquivalentTo(before[i], CollectionOrdering.Matching);
            }
        }
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 0)]
    [Arguments(-1, 5)]
    [Arguments(int.MaxValue, int.MaxValue)]
    public async Task InvalidOutputSize_IsRejectedBeforeAnyWork(int width, int height)
    {
        using Image image = Numbered(2, 2);
        bool processed = false;
        var processor = new ScriptedProcessor(_ => new Size(width, height), _ => processed = true);
        Assert.ThrowsExactly<InvalidOperationException>(() => image.Mutate(x => x.ApplyProcessor(processor)));
        await Assert.That(processed).IsFalse();
        await Assert.That(image.Size).IsEqualTo(new Size(2, 2));
    }

    [Test]
    public void NullProcessor_IsRejected()
    {
        using Image image = Numbered(2, 2);
        Assert.ThrowsExactly<ArgumentNullException>(() => image.Mutate(x => x.ApplyProcessor<IFrameProcessor>(null!)));
    }
}
