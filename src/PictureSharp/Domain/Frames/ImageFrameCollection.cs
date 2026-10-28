using System.Collections;
using PictureSharp.Domain.Pixels;

namespace PictureSharp;

/// <summary>
/// The frames of an <see cref="Image"/>. All frames share the image's dimensions and <see cref="Image.PixelType"/>; an
/// image always has at least one frame. An inserted 8-bit frame is widened exactly on a 16-bit image; a 16-bit frame
/// cannot be inserted into an 8-bit image until one of them is converted explicitly.
/// </summary>
public sealed class ImageFrameCollection : IReadOnlyList<ImageFrame>
{
    private readonly Image _owner;
    private readonly List<ImageFrame> _frames;

    internal ImageFrameCollection(Image owner, List<ImageFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames[0]);
        int width = frames[0].Width;
        int height = frames[0].Height;
        PixelType pixelType = frames[0].PixelType;
        HashSet<ImageFrame>? seen = frames.Count > 1 ? [] : null;
        foreach (ImageFrame frame in frames)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.IsOwned || (seen is not null && !seen.Add(frame)))
                throw new ArgumentException("A frame can belong to only one image.", nameof(frames));
            if (frame.Width != width || frame.Height != height)
                throw new ArgumentException("All frames must have the same size.", nameof(frames));
            if (frame.PixelType != pixelType)
                throw new ArgumentException("All frames must have the same pixel type.", nameof(frames));
        }
        _owner = owner;
        _frames = frames;
        foreach (ImageFrame frame in frames) frame.Attach(owner.FrameOwner);
    }

    /// <inheritdoc/>
    public int Count { get { _owner.ThrowIfDisposed(); return _frames.Count; } }

    /// <inheritdoc/>
    public ImageFrame this[int index] { get { _owner.ThrowIfDisposed(); return _frames[index]; } }

    /// <summary>The first frame; pixel accessors on <see cref="Image"/> work on it.</summary>
    public ImageFrame RootFrame => this[0];

    internal void Release()
    {
        foreach (ImageFrame frame in _frames) frame.Detach(_owner.FrameOwner);
        _frames.Clear();
    }

    internal void AcceptTransferredFrame(ImageFrame frame)
    {
        _owner.AttachedGainMap = null;
        _frames[0].Detach(_owner.FrameOwner);
        _frames[0] = frame;
        frame.Attach(_owner.FrameOwner);
    }

    /// <summary>
    /// Takes ownership of an unattached frame. Its size must match the image size; transparent pixels set
    /// <see cref="Image.HasAlpha"/>. An 8-bit frame is widened exactly when the image stores <see cref="Rgba64"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The frame is owned or has another size, or it stores <see cref="Rgba64"/> while the image stores
    /// <see cref="Rgba32"/>: convert one of them explicitly first.
    /// </exception>
    public void Add(ImageFrame frame) => Insert(Count, frame);

    /// <summary>
    /// Appends an independent copy, without transferring or sharing the source frame. The copy, not the source, is
    /// widened when the image stores <see cref="Rgba64"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The frame has another size or stores <see cref="Rgba64"/> while the image stores <see cref="Rgba32"/>.</exception>
    public ImageFrame AddClone(ImageFrame frame)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        ValidateSizeAndDepth(_frames.Count, frame);
        ImageFrame clone = CopyAtImageDepth(frame);
        Insert(_frames.Count, clone, !_owner.HasAlpha && !clone.IsOpaque());
        return clone;
    }

    /// <summary>
    /// Appends a copy of the root frame of <paramref name="image"/> with the given duration, widened when this image
    /// stores <see cref="Rgba64"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The image has another size or stores <see cref="Rgba64"/> while this image stores <see cref="Rgba32"/>.</exception>
    public ImageFrame Add(Image image, TimeSpan duration)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(image);
        ImageFrame source = image.Frames.RootFrame;
        ValidateSizeAndDepth(_frames.Count, source);
        ImageFrame frame = CopyAtImageDepth(source);
        frame.Duration = duration;
        Insert(_frames.Count, frame, image.HasAlpha);
        return frame;
    }

    /// <summary>
    /// Takes ownership of an unattached frame at <paramref name="index"/>. Its size must match the image size.
    /// A frame with transparent pixels sets <see cref="Image.HasAlpha"/>. An 8-bit frame is widened exactly when the
    /// image stores <see cref="Rgba64"/>.
    /// </summary>
    /// <inheritdoc cref="Add(ImageFrame)"/>
    public void Insert(int index, ImageFrame frame)
    {
        _owner.ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.IsOwned) throw new ArgumentException("The frame already belongs to an owner. Use AddClone to copy it.", nameof(frame));
        ValidateInsertion(index, frame);
        if (frame.PixelType != _owner.PixelType)
        {
            frame.WidenUnowned();
        }

        Insert(index, frame, !_owner.HasAlpha && !frame.IsOpaque());
    }

    /// <summary>A copy of <paramref name="frame"/> with the image's pixel type; the depth was validated.</summary>
    private ImageFrame CopyAtImageDepth(ImageFrame frame)
    {
        if (frame.PixelType == _owner.PixelType)
            return frame.Clone();
        ImageFrame copy = ImageFrame.CreateUninitialized(frame.Width, frame.Height, PixelType.Rgba64);
        PixelDepth.Widen(frame.Pixels, copy.GetPixelSpan<Rgba64>());
        copy.Duration = frame.Duration;
        return copy;
    }

    private void ValidateSizeAndDepth(int index, ImageFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)index, (uint)_frames.Count, nameof(index));
        if (frame.Width != _frames[0].Width || frame.Height != _frames[0].Height)
        {
            throw new ArgumentException(
                $"Frame size {frame.Width}x{frame.Height} does not match image size {_frames[0].Width}x{_frames[0].Height}.",
                nameof(frame));
        }

        if (frame.PixelType == PixelType.Rgba64 && _owner.PixelType == PixelType.Rgba32)
        {
            throw new ArgumentException(
                "A 16-bit frame cannot be added to an 8-bit image without losing precision. Convert the frame's image with "
                + "ConvertPixelType(PixelType.Rgba32), or this image with ConvertPixelType(PixelType.Rgba64), first.",
                nameof(frame));
        }
    }

    private void ValidateInsertion(int index, ImageFrame frame)
    {
        ValidateSizeAndDepth(index, frame);
        if (frame.IsOwned)
        {
            throw new ArgumentException("The frame already belongs to an image. Use AddClone to copy it.", nameof(frame));
        }

    }

    private void Insert(int index, ImageFrame frame, bool hasAlpha)
    {
        ValidateInsertion(index, frame);
        _owner.AttachedGainMap = null;
        _frames.Insert(index, frame);
        frame.Attach(_owner.FrameOwner);
        _owner.HasAlpha |= hasAlpha;
    }

    /// <summary>Removes the frame at <paramref name="index"/>. The last remaining frame cannot be removed.</summary>
    public void RemoveAt(int index)
    {
        _owner.ThrowIfDisposed();
        if (_frames.Count == 1)
        {
            throw new InvalidOperationException("An image must contain at least one frame.");
        }

        ImageFrame removed = _frames[index];
        _owner.AttachedGainMap = null;
        _frames.RemoveAt(index);
        removed.Detach(_owner.FrameOwner);
    }

    /// <summary>Moves a frame from one position to another.</summary>
    public void Move(int fromIndex, int toIndex)
    {
        _owner.ThrowIfDisposed();
        ImageFrame frame = _frames[fromIndex];
        if ((uint)toIndex >= (uint)_frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(toIndex));
        }

        _owner.AttachedGainMap = null;
        _frames.RemoveAt(fromIndex);
        _frames.Insert(toIndex, frame);
    }

    /// <summary>Returns a new still image with a copy of the frame at <paramref name="index"/> and the image's metadata.</summary>
    public Image CloneFrame(int index)
    {
        _owner.ThrowIfDisposed();
        ImageFrame frame = _frames[index].Clone();
        return _owner.CopyPropertiesTo(new Image([frame]));
    }

    /// <summary>
    /// Removes the frame at <paramref name="index"/> and returns it as a new still image with the image's metadata.
    /// The last remaining frame cannot be exported.
    /// </summary>
    public Image ExportFrame(int index)
    {
        _owner.ThrowIfDisposed();
        ImageFrame frame = _frames[index];
        if (_frames.Count == 1) throw new InvalidOperationException("An image must contain at least one frame.");
        // Prepare allocations and metadata before transferring ownership from the source.
        var target = new Image([new ImageFrame(1, 1, frame.PixelType)]);
        _owner.CopyPropertiesTo(target);
        RemoveAt(index);
        target.AcceptTransferredFrame(frame);
        return target;
    }

    /// <summary>Index of the frame, or -1.</summary>
    public int IndexOf(ImageFrame frame) { _owner.ThrowIfDisposed(); return _frames.IndexOf(frame); }

    /// <inheritdoc/>
    public IEnumerator<ImageFrame> GetEnumerator() { _owner.ThrowIfDisposed(); return _frames.GetEnumerator(); }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
