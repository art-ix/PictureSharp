using System.Drawing;

namespace PictureSharp.Processing;

/// <summary>
/// A custom operation that produces a new pixel buffer for every frame, possibly of another size, run by
/// <see cref="ImageProcessingContext.ApplyProcessor{TProcessor}(TProcessor)"/>. Implement it as a struct to avoid
/// allocations, or as a class; it must not keep the spans after <see cref="Process"/> returns.
/// </summary>
/// <example>
/// <code>
/// readonly struct MirrorHalf : IFrameProcessor
/// {
///     public Size GetOutputSize(Size source) => new(source.Width / 2, source.Height);
///     public void Process(ReadOnlySpan&lt;Rgba32&gt; source, Size sourceSize, Span&lt;Rgba32&gt; destination, Size destinationSize, CancellationToken cancellationToken)
///     {
///         for (int y = 0; y &lt; destinationSize.Height; y++)
///             source.Slice(y * sourceSize.Width, destinationSize.Width).CopyTo(destination.Slice(y * destinationSize.Width));
///     }
/// }
///
/// image.Mutate(x => x.ApplyProcessor(new MirrorHalf()));
/// </code>
/// </example>
public interface IFrameProcessor
{
    /// <summary>
    /// The size of the output for a canvas of <paramref name="source"/> size. It is asked once per operation, because all
    /// frames of an image share one canvas; both sides must be positive.
    /// </summary>
    Size GetOutputSize(Size source);

    /// <summary>
    /// Writes one frame's output. <paramref name="source"/> and <paramref name="destination"/> are row-major with
    /// straight alpha; the destination starts transparent black. Long work should observe
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    void Process(ReadOnlySpan<Rgba32> source, Size sourceSize, Span<Rgba32> destination, Size destinationSize, CancellationToken cancellationToken);
}
