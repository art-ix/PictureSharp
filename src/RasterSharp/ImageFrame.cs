using System.Drawing;
using System.Runtime.InteropServices;

namespace RasterSharp;

/// <summary>
/// One frame of an <see cref="Image"/>: a full-canvas RGBA pixel buffer plus its display duration.
/// </summary>
public sealed partial class ImageFrame
{
    private Rgba32[] _pixels;

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
    public TimeSpan Duration { get; set; }

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

    internal void Replace(int width, int height, Rgba32[] pixels)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
    }

    internal static void ValidateSize(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        if ((long)width * height > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image is too large.");
        }
    }

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
