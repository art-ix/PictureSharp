using System.Collections;

namespace RasterSharp;

/// <summary>The frames of an <see cref="Image"/>. All frames share the image's dimensions; an image always has at least one frame.</summary>
public sealed class ImageFrameCollection : IReadOnlyList<ImageFrame>
{
    private readonly Image _owner;
    private readonly List<ImageFrame> _frames;

    internal ImageFrameCollection(Image owner, List<ImageFrame> frames)
    {
        _owner = owner;
        _frames = frames;
    }

    /// <inheritdoc/>
    public int Count => _frames.Count;

    /// <inheritdoc/>
    public ImageFrame this[int index] => _frames[index];

    internal List<ImageFrame> List => _frames;

    /// <summary>Appends a frame. Its size must match the image size.</summary>
    public void Add(ImageFrame frame) => Insert(_frames.Count, frame);

    /// <summary>Appends a copy of the active frame of <paramref name="image"/> with the given duration.</summary>
    public ImageFrame Add(Image image, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageFrame frame = image.ActiveFrame.Clone();
        frame.Duration = duration;
        Add(frame);
        return frame;
    }

    /// <summary>Inserts a frame at <paramref name="index"/>. Its size must match the image size.</summary>
    public void Insert(int index, ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_frames.Count > 0 && (frame.Width != _frames[0].Width || frame.Height != _frames[0].Height))
        {
            throw new ArgumentException(
                $"Frame size {frame.Width}x{frame.Height} does not match image size {_frames[0].Width}x{_frames[0].Height}.",
                nameof(frame));
        }

        if (_frames.Contains(frame))
        {
            throw new ArgumentException("The frame is already part of this image.", nameof(frame));
        }

        _frames.Insert(index, frame);
    }

    /// <summary>Removes the frame at <paramref name="index"/>. The last remaining frame cannot be removed.</summary>
    public void RemoveAt(int index)
    {
        if (_frames.Count == 1)
        {
            throw new InvalidOperationException("An image must contain at least one frame.");
        }

        _frames.RemoveAt(index);
        _owner.OnFrameRemoved(index);
    }

    /// <summary>Moves a frame from one position to another.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        ImageFrame frame = _frames[fromIndex];
        if ((uint)toIndex >= (uint)_frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        }

        _frames.RemoveAt(fromIndex);
        _frames.Insert(toIndex, frame);
    }

    /// <summary>Index of the frame, or -1.</summary>
    public int IndexOf(ImageFrame frame) => _frames.IndexOf(frame);

    /// <inheritdoc/>
    public IEnumerator<ImageFrame> GetEnumerator() => _frames.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
