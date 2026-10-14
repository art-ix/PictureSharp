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

    /// <summary>The first frame; pixel accessors on <see cref="Image"/> work on it.</summary>
    public ImageFrame RootFrame => _frames[0];

    internal List<ImageFrame> List => _frames;

    /// <summary>Appends a frame. Its size must match the image size; transparent pixels set <see cref="Image.HasAlpha"/>.</summary>
    public void Add(ImageFrame frame) => Insert(_frames.Count, frame);

    /// <summary>Appends a copy of the root frame of <paramref name="image"/> with the given duration.</summary>
    public ImageFrame Add(Image image, TimeSpan duration)
    {
        ArgumentNullException.ThrowIfNull(image);
        ImageFrame frame = image.Frames.RootFrame.Clone();
        frame.Duration = duration;
        Insert(_frames.Count, frame, image.HasAlpha);
        return frame;
    }

    /// <summary>
    /// Inserts a frame at <paramref name="index"/>. Its size must match the image size.
    /// A frame with transparent pixels sets <see cref="Image.HasAlpha"/>.
    /// </summary>
    public void Insert(int index, ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Insert(index, frame, !_owner.HasAlpha && !frame.IsOpaque());
    }

    private void Insert(int index, ImageFrame frame, bool hasAlpha)
    {
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
        _owner.HasAlpha |= hasAlpha;
    }

    /// <summary>Removes the frame at <paramref name="index"/>. The last remaining frame cannot be removed.</summary>
    public void RemoveAt(int index)
    {
        if (_frames.Count == 1)
        {
            throw new InvalidOperationException("An image must contain at least one frame.");
        }

        _frames.RemoveAt(index);
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

    /// <summary>Returns a new still image with a copy of the frame at <paramref name="index"/> and the image's metadata.</summary>
    public Image CloneFrame(int index)
    {
        ImageFrame frame = _frames[index].Clone();
        return _owner.CopyPropertiesTo(new Image([frame]));
    }

    /// <summary>
    /// Removes the frame at <paramref name="index"/> and returns it as a new still image with the image's metadata.
    /// The last remaining frame cannot be exported.
    /// </summary>
    public Image ExportFrame(int index)
    {
        ImageFrame frame = _frames[index];
        RemoveAt(index);
        return _owner.CopyPropertiesTo(new Image([frame]));
    }

    /// <summary>Index of the frame, or -1.</summary>
    public int IndexOf(ImageFrame frame) => _frames.IndexOf(frame);

    /// <inheritdoc/>
    public IEnumerator<ImageFrame> GetEnumerator() => _frames.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
