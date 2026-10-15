using System.Drawing;

namespace PictureSharp;

/// <summary>
/// A decoded raster image. Pixels are stored as <see cref="Rgba32"/> with straight alpha; animated images hold several
/// full-canvas <see cref="Frames"/>. Load images with <see cref="Load(string, DecoderOptions?)"/>, transform them with
/// <see cref="Mutate(Action{ImageProcessingContext})"/> (in place) or <see cref="Clone(Action{ImageProcessingContext})"/> (a new image), and write them
/// with <see cref="Save(string)"/> or the <c>SaveAs*</c> methods.
/// </summary>
/// <example>
/// <code>
/// using var image = Image.Load("photo.jpg");
/// image.Mutate(x => x.AutoOrient().Resize(1200, 0));
/// image.SaveAsWebP("photo.webp", new() { Quality = 82 });
/// </code>
/// </example>
public sealed partial class Image : IDisposable
{
    private readonly ImageFrameCollection _frames;
    private bool _disposed;

    /// <summary>Creates a fully transparent image.</summary>
    public Image(int width, int height)
        : this([new ImageFrame(width, height)])
    {
        HasAlpha = true;
    }

    /// <summary>Creates an image filled with <paramref name="background"/>.</summary>
    public Image(int width, int height, Rgba32 background)
        : this(width, height)
    {
        RootFrame.GetPixelSpan().Fill(background);
        HasAlpha = background.A != 255;
    }

    internal Image(List<ImageFrame> frames)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("An image needs at least one frame.", nameof(frames));
        }

        _frames = new ImageFrameCollection(this, frames);
    }

    /// <summary>Width in pixels.</summary>
    public int Width => RootFrame.Width;

    /// <summary>Height in pixels.</summary>
    public int Height => RootFrame.Height;

    /// <summary>Width and height.</summary>
    public Size Size => new(Width, Height);

    /// <summary>The rectangle (0, 0, <see cref="Width"/>, <see cref="Height"/>).</summary>
    public Rectangle Bounds => new(0, 0, Width, Height);

    /// <summary>The format the image was decoded from, or null for images created in memory.</summary>
    public ImageFormat? SourceFormat { get; internal set; }

    /// <summary>Stored size and scale denominator when the decoder produced a reduced image for <see cref="DecoderOptions.TargetSize"/>.</summary>
    internal (System.Drawing.Size Size, int Scale)? ReducedDecode { get; set; }

    /// <summary>
    /// Alpha-channel hint maintained by decoding, processing, frame insertion and <c>CopyPixelDataFrom</c>.
    /// Writes through pixel views and <see cref="ImageFrame"/> copy methods do not update it.
    /// Use <see cref="IsOpaque"/> to check whether any pixel is actually transparent.
    /// </summary>
    public bool HasAlpha { get; internal set; }

    /// <summary>Resolution, ICC profile, EXIF and XMP metadata.</summary>
    public ImageMetadata Metadata { get; internal set; } = new();

    /// <summary>The frames of the image (one for still images).</summary>
    public ImageFrameCollection Frames
    {
        get
        {
            ThrowIfDisposed();
            return _frames;
        }
    }

    /// <summary>Number of frames.</summary>
    public int FrameCount => Frames.Count;

    /// <summary>Whether the image has more than one frame.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>Number of times an animation repeats; 0 means forever.</summary>
    public int LoopCount
    {
        get;
        set => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(LoopCount), value, "Expected a non-negative number.");
    }

    /// <summary>Suggested canvas background color for animations (a hint; viewers often ignore it).</summary>
    public Rgba32 AnimationBackgroundColor { get; set; }

    internal ImageFrame RootFrame => Frames.RootFrame;

    /// <summary>Releases the pixel buffers. Further use of the image throws <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frames.List.Clear();
    }

    /// <inheritdoc/>
    public override string ToString() =>
        _disposed ? "Image (disposed)" : $"Image {Width}x{Height}, {SourceFormat?.Name ?? "in memory"}, frames: {FrameCount}";

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal Image CopyPropertiesTo(Image target)
    {
        target.SourceFormat = SourceFormat;
        target.HasAlpha = HasAlpha;
        target.Metadata = Metadata.Clone();
        target.LoopCount = LoopCount;
        target.AnimationBackgroundColor = AnimationBackgroundColor;
        return target;
    }

    internal static void CheckRectangle(Rectangle rect, int width, int height, string paramName)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || rect.Right > width || rect.Bottom > height)
        {
            throw new ArgumentOutOfRangeException(paramName, rect, $"The rectangle must lie within the image bounds {width}x{height}.");
        }
    }
}
