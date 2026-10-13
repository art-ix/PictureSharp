namespace RasterSharp.Processing;

/// <summary>
/// The image an <see cref="ImageProcessingContext"/> works on. For <see cref="Image.Clone(Action{ImageProcessingContext})"/>
/// the frames start out sharing the source's pixel buffers: operations that produce new buffers (resize, crop, odd
/// rotations) read the source directly, and the first in-place operation copies the buffers.
/// </summary>
internal sealed class ProcessingState(Image image, bool shared)
{
    private bool _shared = shared;

    public Image Image { get; } = image;

    /// <summary>Frames for reading; their buffers must not be written.</summary>
    public ImageFrameCollection Frames => Image.Frames;

    /// <summary>Frames whose buffers may be written in place.</summary>
    public ImageFrameCollection WritableFrames
    {
        get
        {
            EnsureWritable();
            return Image.Frames;
        }
    }

    /// <summary>Records that every frame got a new buffer.</summary>
    public void FramesReplaced() => _shared = false;

    public void EnsureWritable()
    {
        if (!_shared)
        {
            return;
        }

        foreach (ImageFrame frame in Image.Frames)
        {
            frame.Replace(frame.Width, frame.Height, (Rgba32[])frame.Pixels.Clone());
        }

        _shared = false;
    }
}
