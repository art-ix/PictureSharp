using PictureSharp.Processing;

namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>
    /// Applies the operations in <paramref name="operation"/> to this image, in order and on every frame.
    /// If an operation throws, the image keeps the changes made by the operations before it.
    /// </summary>
    /// <example>
    /// <code>image.Mutate(x => x.AutoOrient().Resize(800, 0).Grayscale());</code>
    /// </example>
    public void Mutate(Action<ImageProcessingContext> operation) => Mutate(operation, CancellationToken.None);

    /// <summary>
    /// Applies the operations in <paramref name="operation"/>; <paramref name="cancellationToken"/> stops them
    /// between frames and, in long operations, between rows, with <see cref="OperationCanceledException"/>.
    /// After a cancellation the image stays valid: operations that produce new buffers (resize, crop, rotation, blur)
    /// leave the frames they had not finished unchanged, but the frames of an in-place operation (color changes,
    /// <see cref="ImageProcessingContext.Apply"/>) may be partly processed. Use
    /// <see cref="Clone(Action{ImageProcessingContext}, CancellationToken)"/> when the source must stay intact.
    /// </summary>
    public void Mutate(Action<ImageProcessingContext> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        operation(new ImageProcessingContext(new ProcessingState(this, shared: false, cancellationToken)));
    }

    /// <summary>
    /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
    /// The source pixels are only copied when an operation needs to change them in place, so
    /// <c>Clone(x => x.Resize(...))</c> does not copy the full-size image first.
    /// </summary>
    /// <example>
    /// <code>using Image thumbnail = image.Clone(x => x.Resize(160, 160, ResizeMode.Crop));</code>
    /// </example>
    public Image Clone(Action<ImageProcessingContext> operation) => Clone(operation, CancellationToken.None);

    /// <summary>
    /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
    /// <paramref name="cancellationToken"/> stops the operations; the partly processed copy is then disposed.
    /// </summary>
    public Image Clone(Action<ImageProcessingContext> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(new ImageFrame(frame.Width, frame.Height, frame.Pixels) { Duration = frame.Duration });
        }

        Image clone = CopyPropertiesTo(new Image(frames));
        var state = new ProcessingState(clone, shared: true, cancellationToken);
        try
        {
            operation(new ImageProcessingContext(state));
            state.EnsureWritable();
        }
        catch
        {
            clone.Dispose();
            throw;
        }

        return clone;
    }

    /// <summary>Returns a deep copy of the image, including all frames and metadata.</summary>
    public Image Clone()
    {
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(frame.Clone());
        }

        return CopyPropertiesTo(new Image(frames));
    }
}
