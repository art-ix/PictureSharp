using PictureSharp.Processing;

namespace PictureSharp.Application.Processing;

internal static class ImageProcessingService
{
    /// <summary>
    /// Applies the operations in <paramref name="operation"/> to this image, in order and on every frame.
    /// If an operation throws, the image keeps the changes made by the operations before it.
    /// </summary>
    /// <example>
    /// <code>ImageProcessingService.Mutate(image, x => x.AutoOrient().Resize(800, 0).Grayscale());</code>
    /// </example>
    public static void Mutate(Image image, Action<ImageProcessingContext> operation) => Mutate(image, operation, CancellationToken.None);

    /// <summary>
    /// Applies the operations in <paramref name="operation"/>; <paramref name="cancellationToken"/> stops them
    /// between frames and, in long operations, between rows, with <see cref="OperationCanceledException"/>.
    /// After a cancellation the image stays valid: operations that produce new buffers (resize, crop, rotation, blur)
    /// publish all frames only after successful preparation, but the frames of an in-place operation (color changes,
    /// <see cref="ImageProcessingContext.Apply"/>) may be partly processed. Use
    /// <see cref="ImageProcessingExtensions.Clone(Image, Action{ImageProcessingContext}, CancellationToken)"/> when the source must stay intact.
    /// </summary>
    public static void Mutate(Image image, Action<ImageProcessingContext> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(image);
        image.ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        operation(new ImageProcessingContext(new ProcessingState(image, shared: false, cancellationToken)));
    }

    /// <summary>
    /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
    /// The source pixels are only copied when an operation needs to change them in place, so
    /// <c>Clone(x => x.Resize(...))</c> does not copy the full-size image first.
    /// </summary>
    /// <example>
    /// <code>using Image thumbnail = image.Clone(x => x.Resize(160, 160, ResizeMode.Crop));</code>
    /// </example>
    public static Image Clone(Image image, Action<ImageProcessingContext> operation) => Clone(image, operation, CancellationToken.None);

    /// <summary>
    /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
    /// <paramref name="cancellationToken"/> stops the operations; the partly processed copy is then disposed.
    /// </summary>
    public static Image Clone(Image image, Action<ImageProcessingContext> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        var frames = new List<ImageFrame>(image.FrameCount);
        foreach (ImageFrame frame in image.Frames)
        {
            frames.Add(new ImageFrame(frame.Width, frame.Height, frame.Pixels) { Duration = frame.Duration });
        }

        Image clone = image.CopyPropertiesTo(new Image(frames));
        clone.GainMap = image.GainMap;   // immutable, shared; geometric edits replace it, other operations remove it
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

}
