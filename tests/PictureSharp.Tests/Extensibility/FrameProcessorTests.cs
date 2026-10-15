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

    [Fact]
    public void StructProcessor_ChangesCanvasSize_AndWritesExpectedPixels()
    {
        using Image image = Numbered(3, 2);
        image.Mutate(x => x.Transpose());
        Assert.Equal(new Size(2, 3), image.Size);
        // Source rows (0 1 2) and (3 4 5) become columns.
        Assert.Equal([0, 3, 1, 4, 2, 5], image.GetPixelSpan().ToArray().Select(p => (int)p.R));
    }

    [Fact]
    public void Processor_ChainsWithBuiltInOperations_AndRunsOnEveryFrame()
    {
        using Image first = Numbered(4, 2);
        using Image second = Numbered(4, 2, offset: 100);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(70), first, second);
        animation.Mutate(x => x.Transpose().Flip(FlipMode.Horizontal).Transpose());

        Assert.Equal(new Size(4, 2), animation.Size);
        Assert.All(animation.Frames, frame => Assert.Equal(TimeSpan.FromMilliseconds(70), frame.Duration));
        // Transpose, horizontal flip and transpose again amount to a vertical flip.
        Assert.Equal([4, 5, 6, 7, 0, 1, 2, 3], animation.Frames[0].GetPixelSpan().ToArray().Select(p => (int)p.R));
        Assert.Equal(104, animation.Frames[1].GetPixelSpan()[0].R);
    }

    [Fact]
    public void Clone_LeavesSourceUnchanged()
    {
        using Image source = Numbered(3, 2);
        Rgba32[] before = source.GetPixelSpan().ToArray();
        using Image transposed = source.Clone(x => x.ApplyProcessor(new TransposeProcessor()));
        Assert.Equal(new Size(2, 3), transposed.Size);
        Assert.Equal(new Size(3, 2), source.Size);
        Assert.Equal(before, source.GetPixelSpan().ToArray());
    }

    [Fact]
    public void Destination_StartsTransparentBlack()
    {
        using var image = new Image(2, 2, new Rgba32(9, 9, 9));
        image.Mutate(x => x.ApplyProcessor(new ScriptedProcessor(_ => new Size(3, 1))));
        Assert.Equal(new Size(3, 1), image.Size);
        Assert.All(image.GetPixelSpan().ToArray(), p => Assert.Equal(default, p));
    }

    [Fact]
    public void FailureOrCancellation_OnALaterFrame_KeepsEveryFrame()
    {
        using Image first = Numbered(4, 2);
        using Image second = Numbered(4, 2, offset: 50);
        using Image animation = Image.CreateAnimation(TimeSpan.FromMilliseconds(40), first, second);
        Rgba32[][] before = [.. animation.Frames.Select(f => f.GetPixelSpan().ToArray())];

        var failing = new ScriptedProcessor(s => new Size(1, 1), frame => { if (frame == 1) throw new InvalidDataException("boom"); });
        Assert.Throws<InvalidDataException>(() => animation.Mutate(x => x.ApplyProcessor(failing)));
        AssertUnchanged();

        using var cts = new CancellationTokenSource();
        var canceling = new ScriptedProcessor(s => new Size(1, 1), frame => { if (frame == 1) cts.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => animation.Mutate(x => x.ApplyProcessor(canceling), cts.Token));
        AssertUnchanged();

        void AssertUnchanged()
        {
            Assert.Equal(new Size(4, 2), animation.Size);
            for (int i = 0; i < before.Length; i++)
            {
                Assert.Equal(before[i], animation.Frames[i].GetPixelSpan().ToArray());
            }
        }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 5)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void InvalidOutputSize_IsRejectedBeforeAnyWork(int width, int height)
    {
        using Image image = Numbered(2, 2);
        bool processed = false;
        var processor = new ScriptedProcessor(_ => new Size(width, height), _ => processed = true);
        Assert.Throws<InvalidOperationException>(() => image.Mutate(x => x.ApplyProcessor(processor)));
        Assert.False(processed);
        Assert.Equal(new Size(2, 2), image.Size);
    }

    [Fact]
    public void NullProcessor_IsRejected()
    {
        using Image image = Numbered(2, 2);
        Assert.Throws<ArgumentNullException>(() => image.Mutate(x => x.ApplyProcessor<IFrameProcessor>(null!)));
    }
}
