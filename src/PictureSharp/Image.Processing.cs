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
    public void Mutate(Action<ImageProcessingContext> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfDisposed();
        operation(new ImageProcessingContext(new ProcessingState(this, shared: false)));
    }

    /// <summary>
    /// Returns a new image with the operations in <paramref name="operation"/> applied; this image is not modified.
    /// The source pixels are only copied when an operation needs to change them in place, so
    /// <c>Clone(x => x.Resize(...))</c> does not copy the full-size image first.
    /// </summary>
    /// <example>
    /// <code>using Image thumbnail = image.Clone(x => x.Resize(160, 160, ResizeMode.Crop));</code>
    /// </example>
    public Image Clone(Action<ImageProcessingContext> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(new ImageFrame(frame.Width, frame.Height, frame.Pixels) { Duration = frame.Duration });
        }

        Image clone = CopyPropertiesTo(new Image(frames));
        var state = new ProcessingState(clone, shared: true);
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
