using PictureSharp.Application.Hdr;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Hdr;
using PictureSharp.Processing;

namespace PictureSharp.Application.Processing;

/// <summary>
/// The image an <see cref="ImageProcessingContext"/> works on. For <see cref="ImageProcessingExtensions.Clone(Image, Action{ImageProcessingContext})"/>
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

    /// <summary>
    /// Frames whose buffers may be written in place. Cancellation is checked before each frame. Removes the gain map,
    /// which describes the pixels before the change.
    /// </summary>
    public FrameSequence WritableFrames
    {
        get
        {
            Image.AttachedGainMap = null;
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

        for (int i = 0; i < Image.FrameCount; i++)
        {
            Image.ReplaceFramePixels(i, Image.Frames[i].Buffer.Clone());
        }

        _shared = false;
    }

    /// <summary>
    /// The attached gain map after <paramref name="edit"/> of the current image, or null when none is attached. Runs
    /// before the frames change, so a failure or cancellation leaves the image as it was.
    /// </summary>
    public GainMapAttachment? EditGainMap(GainMapEdit edit) =>
        Image.AttachedGainMap is { } attachment ? GainMapEditing.Apply(attachment, Image.Size, edit, CancellationToken) : null;

    /// <summary>
    /// Commits every output together, preserving canvas invariants on cancellation or failure. The gain map follows
    /// <paramref name="gainMapEdit"/>, or is removed without one.
    /// </summary>
    public void ReplaceFrames<TState>(TState state, Func<ImageFrame, TState, CancellationToken, FrameReplacement> transform,
        GainMapEdit? gainMapEdit = null)
    {
        int count = Image.FrameCount;
        if (count == 1)
        {
            CancellationToken.ThrowIfCancellationRequested();
            ImageFrame frame = Image.Frames[0];
            FrameReplacement result = transform(frame, state, CancellationToken);
            GainMapAttachment? gainMap = gainMapEdit is { } edit ? EditGainMap(edit) : null;
            CancellationToken.ThrowIfCancellationRequested();
            Image.CommitFrames(new ReadOnlySpan<FrameReplacement>(in result), gainMap);
        }
        else
        {
            FrameReplacement[] results = System.Buffers.ArrayPool<FrameReplacement>.Shared.Rent(count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    CancellationToken.ThrowIfCancellationRequested();
                    results[i] = transform(Image.Frames[i], state, CancellationToken);
                }

                GainMapAttachment? gainMap = gainMapEdit is { } edit ? EditGainMap(edit) : null;
                CancellationToken.ThrowIfCancellationRequested();
                Image.CommitFrames(results.AsSpan(0, count), gainMap);
            }
            finally
            {
                System.Buffers.ArrayPool<FrameReplacement>.Shared.Return(results, clearArray: true);
            }
        }
        FramesReplaced();
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
