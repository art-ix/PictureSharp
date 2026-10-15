namespace PictureSharp.Processing;

/// <summary>
/// The image an <see cref="ImageProcessingContext"/> works on. For <see cref="Image.Clone(Action{ImageProcessingContext})"/>
/// the frames start out sharing the source's pixel buffers: operations that produce new buffers (resize, crop, odd
/// rotations) read the source directly, and the first in-place operation copies the buffers.
/// </summary>
internal sealed class ProcessingState(Image image, bool shared, CancellationToken cancellationToken = default)
{
    private bool _shared = shared;

    public Image Image { get; } = image;

    /// <summary>Stops operations between frames and, in long operations, between rows.</summary>
    public CancellationToken CancellationToken { get; } = cancellationToken;

    /// <summary>Frames for reading; their buffers must not be written. Cancellation is checked before each frame.</summary>
    public FrameSequence Frames => new(Image.Frames, CancellationToken);

    /// <summary>Frames whose buffers may be written in place. Cancellation is checked before each frame.</summary>
    public FrameSequence WritableFrames
    {
        get
        {
            EnsureWritable();
            return new(Image.Frames, CancellationToken);
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

    /// <summary>The frames of an image, enumerated without allocation and with a cancellation check before each one.</summary>
    public readonly struct FrameSequence(ImageFrameCollection frames, CancellationToken cancellationToken)
    {
        public Enumerator GetEnumerator() => new(frames, cancellationToken);

        public struct Enumerator(ImageFrameCollection frames, CancellationToken cancellationToken)
        {
            private int _index = -1;

            public readonly ImageFrame Current => frames[_index];

            public bool MoveNext()
            {
                if (++_index >= frames.Count)
                {
                    return false;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
        }
    }
}
