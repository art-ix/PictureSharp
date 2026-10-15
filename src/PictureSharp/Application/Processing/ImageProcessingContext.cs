using System.Drawing;
using PictureSharp.Application.Processing;
using PictureSharp.Processing;

namespace PictureSharp.Processing;

/// <summary>
/// The operations available inside <see cref="ImageProcessingExtensions.Mutate(Image, Action{ImageProcessingContext})"/> and <see cref="ImageProcessingExtensions.Clone(Image, Action{ImageProcessingContext})"/>.
/// Each operation applies to every frame and returns the context, so calls chain in the order they run.
/// Own operations can be added with a C# 14 <c>extension(ImageProcessingContext x)</c> block built on
/// <see cref="Apply"/> and the other operations.
/// </summary>
public readonly ref partial struct ImageProcessingContext
{
    private readonly ProcessingState _state;

    internal ImageProcessingContext(ProcessingState state)
    {
        _state = state;
    }

    /// <summary>The current size of the image (after the operations so far).</summary>
    public Size Size => _state.Image.Size;

    /// <summary>
    /// The token passed to <see cref="ImageProcessingExtensions.Mutate(Image, Action{ImageProcessingContext}, CancellationToken)"/> or
    /// <see cref="ImageProcessingExtensions.Clone(Image, Action{ImageProcessingContext}, CancellationToken)"/>; long custom operations should check it.
    /// </summary>
    public CancellationToken CancellationToken => _state.CancellationToken;

    /// <summary>The number of frames the operations apply to.</summary>
    public int FrameCount => _state.Image.FrameCount;

    private Image Target => _state.Image;

    /// <summary>
    /// Runs <paramref name="operation"/> on every frame, e.g. to edit pixels through <see cref="ImageFrame.GetPixelSpan"/>.
    /// The frame size cannot change.
    /// </summary>
    public ImageProcessingContext Apply(Action<ImageFrame> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        foreach (ImageFrame frame in _state.WritableFrames)
        {
            operation(frame);
        }

        return this;
    }

    private ImageProcessingContext ForEachPixelSpan(SpanAction operation) =>
        ForEachPixelSpan(operation, static (pixels, action) => action(pixels));

    private ImageProcessingContext ForEachPixelSpan<TState>(TState state, SpanAction<TState> operation)
    {
        foreach (ImageFrame frame in _state.WritableFrames)
        {
            operation(frame.GetPixelSpan(), state);
        }

        return this;
    }

    private delegate void SpanAction<TState>(Span<Rgba32> pixels, TState state);

    private delegate void SpanAction(Span<Rgba32> pixels);
}
