using System.Drawing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Images;
using System.Runtime.InteropServices;

namespace PictureSharp;

/// <summary>
/// One frame of an <see cref="Image"/>: a full-canvas RGBA pixel buffer plus its display duration.
/// </summary>
public sealed partial class ImageFrame
{
    private Rgba32[] _pixels;

    private FrameOwner _owner;
    internal bool IsOwned => _owner.IsValid;

    internal void Attach(FrameOwner owner)
    {
        if (!owner.IsValid) throw new ArgumentException("An owner is required.", nameof(owner));
        if (IsOwned) throw new InvalidOperationException("The frame already has an owner.");
        _owner = owner;
    }

    internal void Detach(FrameOwner owner)
    {
        if (!owner.IsValid || !_owner.Matches(owner)) throw new InvalidOperationException("Only the owner can detach a frame.");
        _owner = default;
    }

    /// <summary>Creates a fully transparent frame.</summary>
    public ImageFrame(int width, int height)
    {
        ValidateSize(width, height);
        Width = width;
        Height = height;
        _pixels = new Rgba32[(long)width * height];
    }

    internal ImageFrame(int width, int height, Rgba32[] pixels)
    {
        new ImageDimensions(width, height).ValidateBuffer(pixels);
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; private set; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Width and height.</summary>
    public Size Size => new(Width, Height);

    /// <summary>The rectangle (0, 0, Width, Height).</summary>
    public Rectangle Bounds => new(0, 0, Width, Height);

    /// <summary>How long the frame is displayed in an animation.</summary>
    public TimeSpan Duration
    {
        get;
        set => field = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(Duration), value, "Duration must not be negative.");
    }

    internal Rgba32[] Pixels => _pixels;

    /// <summary>Gets or sets a pixel.</summary>
    public Rgba32 this[int x, int y]
    {
        get
        {
            CheckCoordinates(x, y);
            return _pixels[(y * Width) + x];
        }
        set
        {
            CheckCoordinates(x, y);
            _pixels[(y * Width) + x] = value;
        }
    }

    /// <summary>Returns the pixels of row <paramref name="y"/>.</summary>
    public Span<Rgba32> GetRowSpan(int y)
    {
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return _pixels.AsSpan(y * Width, Width);
    }

    /// <summary>Returns all pixels, row by row, without padding.</summary>
    public Span<Rgba32> GetPixelSpan() => _pixels;

    /// <summary>
    /// Returns a writable view of all RGBA bytes without a copy.
    /// An operation replacing the pixel buffer detaches this view from the frame.
    /// </summary>
    public Span<byte> GetPixelByteSpan() => MemoryMarshal.AsBytes(_pixels.AsSpan());

    /// <summary>Returns a deep copy.</summary>
    public ImageFrame Clone() => new(Width, Height, (Rgba32[])_pixels.Clone()) { Duration = Duration };

    internal static ImageFrame CreateUninitialized(int width, int height)
    {
        ValidateSize(width, height);
        return new ImageFrame(width, height, GC.AllocateUninitializedArray<Rgba32>(checked(width * height)));
    }

    // The aggregate validates the complete batch before calling this non-throwing publication step.
    internal void Publish(FrameReplacement replacement)
    {
        Width = replacement.Width;
        Height = replacement.Height;
        _pixels = replacement.Pixels;
    }

    internal static void ValidateSize(int width, int height) => _ = new ImageDimensions(width, height);

    private void CheckCoordinates(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x), x, "X is outside the image.");
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y), y, "Y is outside the image.");
        }
    }
}
