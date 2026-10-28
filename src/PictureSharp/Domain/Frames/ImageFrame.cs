using System.Drawing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Images;
using PictureSharp.Domain.Pixels;

namespace PictureSharp;

/// <summary>
/// One frame of an <see cref="Image"/>: a full-canvas RGBA pixel buffer of its <see cref="PixelType"/> plus its display
/// duration.
/// </summary>
public sealed partial class ImageFrame
{
    private PixelBuffer _pixels;

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

    /// <summary>Creates a fully transparent frame with <see cref="Rgba32"/> pixels.</summary>
    public ImageFrame(int width, int height)
        : this(width, height, PixelType.Rgba32)
    {
    }

    /// <summary>Creates a fully transparent frame with pixels of <paramref name="pixelType"/>.</summary>
    public ImageFrame(int width, int height, PixelType pixelType)
    {
        ValidateSize(width, height);
        Width = width;
        Height = height;
        _pixels = PixelBuffer.Allocate(PixelTypes.Check(pixelType), width * height, clear: true);
    }

    internal ImageFrame(int width, int height, PixelBuffer pixels)
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

    /// <summary>The storage of the pixels: <see cref="Rgba32"/> or <see cref="Rgba64"/>.</summary>
    public PixelType PixelType => _pixels.PixelType;

    internal PixelBuffer Buffer => _pixels;

    /// <summary>The 8-bit pixels, for 8-bit paths (codecs, ports) that have checked <see cref="PixelType"/>.</summary>
    /// <exception cref="InvalidOperationException">The frame stores <see cref="Rgba64"/> pixels.</exception>
    internal Rgba32[] Pixels => _pixels.Rgba32 ?? throw PixelTypes.Mismatch(PixelType, typeof(Rgba32));

    /// <summary>Row <paramref name="y"/> of <see cref="Pixels"/>.</summary>
    internal Span<Rgba32> PixelRow(int y)
    {
        Rgba32[] pixels = Pixels;
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return pixels.AsSpan(y * Width, Width);
    }

    /// <summary>Gets or sets a pixel of an 8-bit frame; use <see cref="GetPixel{TPixel}"/> for any <see cref="PixelType"/>.</summary>
    /// <exception cref="InvalidOperationException">The frame stores <see cref="Rgba64"/> pixels.</exception>
    public Rgba32 this[int x, int y]
    {
        get
        {
            Rgba32[] pixels = _pixels.Rgba32 ?? throw PixelTypes.Mismatch(PixelType, typeof(Rgba32));
            CheckCoordinates(x, y);
            return pixels[(y * Width) + x];
        }
        set
        {
            Rgba32[] pixels = _pixels.Rgba32 ?? throw PixelTypes.Mismatch(PixelType, typeof(Rgba32));
            CheckCoordinates(x, y);
            pixels[(y * Width) + x] = value;
        }
    }

    /// <summary>Returns the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <typeparam name="TPixel"><see cref="Rgba32"/> or <see cref="Rgba64"/>, matching <see cref="PixelType"/>.</typeparam>
    /// <exception cref="NotSupportedException"><typeparamref name="TPixel"/> is not a storage pixel type.</exception>
    /// <exception cref="InvalidOperationException"><typeparamref name="TPixel"/> does not match <see cref="PixelType"/>.</exception>
    public TPixel GetPixel<TPixel>(int x, int y) where TPixel : unmanaged
    {
        TPixel[] pixels = _pixels.As<TPixel>();
        CheckCoordinates(x, y);
        return pixels[(y * Width) + x];
    }

    /// <summary>Sets the pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public void SetPixel<TPixel>(int x, int y, TPixel value) where TPixel : unmanaged
    {
        TPixel[] pixels = _pixels.As<TPixel>();
        CheckCoordinates(x, y);
        pixels[(y * Width) + x] = value;
    }

    /// <summary>Returns the pixels of row <paramref name="y"/>.</summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public Span<TPixel> GetRowSpan<TPixel>(int y) where TPixel : unmanaged
    {
        TPixel[] pixels = _pixels.As<TPixel>();
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return pixels.AsSpan(y * Width, Width);
    }

    /// <summary>
    /// Returns all pixels, row by row, without padding. An operation replacing the pixel buffer (including a
    /// <see cref="PixelType"/> conversion) detaches this view from the frame.
    /// </summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public Span<TPixel> GetPixelSpan<TPixel>() where TPixel : unmanaged => _pixels.As<TPixel>();

    /// <summary>
    /// Returns a writable view of all pixel bytes in the native layout of <see cref="PixelType"/>, without a copy.
    /// An operation replacing the pixel buffer detaches this view from the frame.
    /// </summary>
    public Span<byte> GetPixelByteSpan() => _pixels.AsBytes();

    /// <summary>Returns a deep copy with the same <see cref="PixelType"/>.</summary>
    public ImageFrame Clone() => new(Width, Height, _pixels.Clone()) { Duration = Duration };

    internal static ImageFrame CreateUninitialized(int width, int height, PixelType pixelType = PixelType.Rgba32)
    {
        ValidateSize(width, height);
        return new ImageFrame(width, height, PixelBuffer.Allocate(pixelType, checked(width * height), clear: false));
    }

    // The aggregate validates the complete batch before calling this non-throwing publication step.
    internal void Publish(FrameReplacement replacement)
    {
        Width = replacement.Width;
        Height = replacement.Height;
        _pixels = replacement.Pixels;
    }

    /// <summary>Widens an unattached 8-bit frame exactly before a 16-bit image takes ownership of it.</summary>
    internal void WidenUnowned()
    {
        if (IsOwned) throw new InvalidOperationException("Only an unattached frame can change its pixel type.");
        var wide = GC.AllocateUninitializedArray<Rgba64>(Width * Height);
        PixelDepth.Widen(Pixels, wide);
        _pixels = wide;
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
