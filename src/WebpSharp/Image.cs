using System.Drawing;
using WebpSharp.Formats;
using WebpSharp.Processing;

namespace WebpSharp;

/// <summary>
/// A decoded raster image, modelled after System.Drawing.Image/Bitmap. Pixels are stored as
/// <see cref="Rgba32"/> with straight alpha. Animated images hold several full-canvas
/// <see cref="Frames"/>; pixel accessors work on the <see cref="ActiveFrame"/>, while transforms
/// (rotate, crop, resize...) apply to all frames in place and return the same instance for chaining.
/// </summary>
public sealed class Image : IDisposable, ICloneable
{
    private readonly ImageFrameCollection _frames;
    private int _activeFrameIndex;
    private bool _disposed;

    /// <summary>Creates a fully transparent image.</summary>
    public Image(int width, int height)
        : this([new ImageFrame(width, height)])
    {
        HasAlpha = true;
    }

    /// <summary>Creates an image filled with <paramref name="background"/>.</summary>
    public Image(int width, int height, Color background)
        : this(width, height)
    {
        Clear(background);
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

    // ---------------------------------------------------------------- loading

    /// <summary>Decodes an image file (WebP, PNG or JPEG; the format is detected from content).</summary>
    public static Image FromFile(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CodecRegistry.Decode(File.ReadAllBytes(path), options ?? DecoderOptions.Default);
    }

    /// <summary>Decodes an image from a stream, reading it to the end. The stream need not be seekable.</summary>
    public static Image FromStream(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CodecRegistry.Decode(StreamHelper.ReadAll(stream), options ?? DecoderOptions.Default);
    }

    /// <summary>Decodes an image from memory.</summary>
    public static Image FromBytes(ReadOnlySpan<byte> data, DecoderOptions? options = null) =>
        CodecRegistry.Decode(data.ToArray(), options ?? DecoderOptions.Default);

    /// <summary>Decodes an image from memory without copying the buffer.</summary>
    public static Image FromBytes(ReadOnlyMemory<byte> data, DecoderOptions? options = null) =>
        CodecRegistry.Decode(data, options ?? DecoderOptions.Default);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public static async Task<Image> FromFileAsync(string path, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return CodecRegistry.Decode(data, options ?? DecoderOptions.Default);
    }

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public static async Task<Image> FromStreamAsync(Stream stream, DecoderOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = await StreamHelper.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return CodecRegistry.Decode(data, options ?? DecoderOptions.Default);
    }

    /// <summary>Creates an image from raw pixel data.</summary>
    /// <param name="pixels">Pixel rows, <paramref name="stride"/> bytes apart.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Layout of <paramref name="pixels"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static Image FromPixels(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, int stride = 0)
    {
        ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
        var image = new Image([frame]) { HasAlpha = format.HasAlpha() };
        image.LoadPixels(pixels, format, stride);
        return image;
    }

    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public static ImageInfo Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CodecRegistry.Identify(File.ReadAllBytes(path), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CodecRegistry.Identify(StreamHelper.ReadAll(stream), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data) => CodecRegistry.Identify(data.ToArray(), DecoderOptions.Default);

    /// <summary>Detects the format from the first bytes of a file (at least 12 bytes are needed for WebP).</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> header) => FormatDetector.Detect(header);

    /// <summary>Builds an animation from the active frames of <paramref name="frames"/>, which must all have the same size.</summary>
    public static Image CreateAnimation(IEnumerable<Image> frames, TimeSpan frameDuration, int loopCount = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfNegative(loopCount);
        var list = new List<ImageFrame>();
        bool hasAlpha = false;
        foreach (Image source in frames)
        {
            ImageFrame frame = source.ActiveFrame.Clone();
            frame.Duration = frameDuration;
            if (list.Count > 0 && (frame.Width != list[0].Width || frame.Height != list[0].Height))
            {
                throw new ArgumentException("All frames must have the same size.", nameof(frames));
            }

            hasAlpha |= source.HasAlpha;
            list.Add(frame);
        }

        if (list.Count == 0)
        {
            throw new ArgumentException("At least one frame is required.", nameof(frames));
        }

        return new Image(list) { HasAlpha = hasAlpha, LoopCount = loopCount };
    }

    // ---------------------------------------------------------------- properties

    /// <summary>Width in pixels.</summary>
    public int Width => Frame0.Width;

    /// <summary>Height in pixels.</summary>
    public int Height => Frame0.Height;

    /// <summary>Width and height.</summary>
    public Size Size => new(Width, Height);

    /// <summary>The format the image was decoded from, or <see cref="ImageFormat.MemoryBmp"/> for images created in memory.</summary>
    public ImageFormat RawFormat { get; internal set; } = ImageFormat.MemoryBmp;

    /// <summary>
    /// Whether the image has an alpha channel (declared by the source format). Use <see cref="IsOpaque"/>
    /// to check whether any pixel is actually transparent.
    /// </summary>
    public bool HasAlpha { get; set; }

    /// <summary>Horizontal resolution in dots per inch.</summary>
    public float HorizontalResolution { get; private set; } = 96f;

    /// <summary>Vertical resolution in dots per inch.</summary>
    public float VerticalResolution { get; private set; } = 96f;

    /// <summary>ICC profile, EXIF and XMP metadata.</summary>
    public ImageMetadata Metadata { get; internal set; } = new();

    /// <summary>Arbitrary user data.</summary>
    public object? Tag { get; set; }

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

    /// <summary>Index of the frame used by the pixel accessors.</summary>
    public int ActiveFrameIndex
    {
        get => _activeFrameIndex;
        set => SelectActiveFrame(value);
    }

    /// <summary>The frame used by the pixel accessors.</summary>
    public ImageFrame ActiveFrame => Frames[_activeFrameIndex];

    /// <summary>Number of times an animation repeats; 0 means forever.</summary>
    public int LoopCount { get; set; }

    /// <summary>Suggested canvas background color for animations (a hint; viewers often ignore it).</summary>
    public Color AnimationBackgroundColor { get; set; } = Color.Transparent;

    private ImageFrame Frame0 => Frames[0];

    /// <summary>Sets the resolution in dots per inch.</summary>
    public void SetResolution(float xDpi, float yDpi)
    {
        if (!(xDpi > 0) || !float.IsFinite(xDpi))
        {
            throw new ArgumentOutOfRangeException(nameof(xDpi));
        }

        if (!(yDpi > 0) || !float.IsFinite(yDpi))
        {
            throw new ArgumentOutOfRangeException(nameof(yDpi));
        }

        HorizontalResolution = xDpi;
        VerticalResolution = yDpi;
    }

    /// <summary>Makes the frame at <paramref name="index"/> the active one.</summary>
    public void SelectActiveFrame(int index)
    {
        if ((uint)index >= (uint)Frames.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, "Frame index out of range.");
        }

        _activeFrameIndex = index;
    }

    internal void OnFrameRemoved(int index)
    {
        if (_activeFrameIndex > index || _activeFrameIndex >= _frames.Count)
        {
            _activeFrameIndex = Math.Max(0, _activeFrameIndex - 1);
        }
    }

    // ---------------------------------------------------------------- pixel access (active frame)

    /// <summary>Gets or sets a pixel of the active frame.</summary>
    public Rgba32 this[int x, int y]
    {
        get => ActiveFrame[x, y];
        set => ActiveFrame[x, y] = value;
    }

    /// <summary>Returns the color of a pixel of the active frame.</summary>
    public Color GetPixel(int x, int y) => ActiveFrame[x, y].ToColor();

    /// <summary>Sets the color of a pixel of the active frame.</summary>
    public void SetPixel(int x, int y, Color color) => ActiveFrame[x, y] = Rgba32.FromColor(color);

    /// <summary>Returns row <paramref name="y"/> of the active frame.</summary>
    public Span<Rgba32> GetRowSpan(int y) => ActiveFrame.GetRowSpan(y);

    /// <summary>Returns all pixels of the active frame, row by row.</summary>
    public Span<Rgba32> GetPixelSpan() => ActiveFrame.GetPixelSpan();

    /// <summary>
    /// Copies a region of the active frame into a pinned buffer in the requested format.
    /// Dispose the result (or call <see cref="UnlockBits"/>) to write changes back.
    /// </summary>
    public BitmapData LockBits(Rectangle rect, ImageLockMode mode, PixelFormat format)
    {
        CheckRectangle(rect, nameof(rect));
        if (mode is not (ImageLockMode.ReadOnly or ImageLockMode.WriteOnly or ImageLockMode.ReadWrite))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return new BitmapData(ActiveFrame, rect, mode, format);
    }

    /// <summary>Writes back and releases data obtained from <see cref="LockBits"/>.</summary>
    public void UnlockBits(BitmapData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        data.Dispose();
    }

    /// <summary>Copies the active frame into <paramref name="destination"/> in the given format.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixels(Span<byte> destination, PixelFormat format, int stride = 0)
    {
        ImageFrame frame = ActiveFrame;
        int rowBytes = frame.Width * format.BytesPerPixel();
        stride = CheckBuffer(destination.Length, rowBytes, frame.Height, stride, nameof(destination));
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConverter.FromRgba(frame.GetRowSpan(y), destination.Slice(y * stride, rowBytes), format);
        }
    }

    /// <summary>Returns the active frame as a tightly packed array in the given format.</summary>
    public byte[] ToPixelArray(PixelFormat format = PixelFormat.Rgba32)
    {
        var result = GC.AllocateUninitializedArray<byte>(checked(Width * Height * format.BytesPerPixel()));
        CopyPixels(result, format);
        return result;
    }

    /// <summary>Overwrites the active frame with pixels from <paramref name="source"/>.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void LoadPixels(ReadOnlySpan<byte> source, PixelFormat format, int stride = 0)
    {
        ImageFrame frame = ActiveFrame;
        int rowBytes = frame.Width * format.BytesPerPixel();
        stride = CheckBuffer(source.Length, rowBytes, frame.Height, stride, nameof(source));
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConverter.ToRgba(source.Slice(y * stride, rowBytes), frame.GetRowSpan(y), format);
        }
    }

    /// <summary>Returns true when every pixel of every frame has alpha 255.</summary>
    public bool IsOpaque()
    {
        foreach (ImageFrame frame in Frames)
        {
            foreach (Rgba32 p in frame.GetPixelSpan())
            {
                if (p.A != 255)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- transforms (in place, all frames)

    /// <summary>Rotates and/or flips all frames.</summary>
    public Image RotateFlip(RotateFlipType rotateFlipType)
    {
        if ((uint)rotateFlipType > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(rotateFlipType));
        }

        foreach (ImageFrame frame in Frames)
        {
            Geometry.RotateFlip(frame, rotateFlipType);
        }

        if (((int)rotateFlipType & 1) != 0)
        {
            (HorizontalResolution, VerticalResolution) = (VerticalResolution, HorizontalResolution);
        }

        return this;
    }

    /// <summary>
    /// Rotates/flips the image according to its EXIF orientation tag and resets the tag to
    /// <see cref="ExifOrientation.TopLeft"/>, so the pixels are stored upright.
    /// </summary>
    public Image AutoOrient()
    {
        ExifOrientation orientation = Metadata.Orientation;
        if (orientation is ExifOrientation.Unknown or ExifOrientation.TopLeft)
        {
            return this;
        }

        RotateFlip(Geometry.FromExifOrientation(orientation));
        Metadata.Orientation = ExifOrientation.TopLeft;
        return this;
    }

    /// <summary>Crops all frames to <paramref name="rect"/>.</summary>
    public Image Crop(Rectangle rect)
    {
        CheckRectangle(rect, nameof(rect));
        foreach (ImageFrame frame in Frames)
        {
            frame.Replace(rect.Width, rect.Height, Geometry.Crop(frame, rect));
        }

        return this;
    }

    /// <summary>Fills all frames with <paramref name="color"/>.</summary>
    public Image Clear(Color color)
    {
        Rgba32 value = Rgba32.FromColor(color);
        foreach (ImageFrame frame in Frames)
        {
            frame.GetPixelSpan().Fill(value);
        }

        return this;
    }

    // ---------------------------------------------------------------- copies

    /// <summary>Returns a deep copy of the image, including all frames and metadata.</summary>
    public Image Clone()
    {
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(frame.Clone());
        }

        return CopyPropertiesTo(new Image(frames));
    }

    /// <summary>Returns a deep copy of a region of the image (all frames).</summary>
    public Image Clone(Rectangle rect)
    {
        CheckRectangle(rect, nameof(rect));
        var frames = new List<ImageFrame>(FrameCount);
        foreach (ImageFrame frame in Frames)
        {
            frames.Add(new ImageFrame(rect.Width, rect.Height, Geometry.Crop(frame, rect)) { Duration = frame.Duration });
        }

        return CopyPropertiesTo(new Image(frames));
    }

    object ICloneable.Clone() => Clone();

    private Image CopyPropertiesTo(Image target)
    {
        target.RawFormat = RawFormat;
        target.HasAlpha = HasAlpha;
        target.HorizontalResolution = HorizontalResolution;
        target.VerticalResolution = VerticalResolution;
        target.Metadata = Metadata.Clone();
        target.Tag = Tag;
        target.LoopCount = LoopCount;
        target.AnimationBackgroundColor = AnimationBackgroundColor;
        target._activeFrameIndex = Math.Min(_activeFrameIndex, target.FrameCount - 1);
        return target;
    }

    // ---------------------------------------------------------------- saving

    /// <summary>Saves the image; the format is chosen from the file extension (.webp: lossy quality 75, .png, .bmp).</summary>
    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        ImageFormat format = ImageFormat.FromExtension(path)
            ?? throw new NotSupportedException($"Cannot determine the image format from the extension of '{path}'.");
        Save(path, DefaultOptions(format));
    }

    /// <summary>Saves the image in the given format with default options.</summary>
    public void Save(string path, ImageFormat format) => Save(path, DefaultOptions(format));

    /// <summary>Saves the image in the given format with default options.</summary>
    public void Save(Stream stream, ImageFormat format) => Save(stream, DefaultOptions(format));

    /// <summary>Saves the image with the given encoder options.</summary>
    public void Save(string path, EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        byte[] data = Encode(options);
        File.WriteAllBytes(path, data);
    }

    /// <summary>Saves the image with the given encoder options.</summary>
    public void Save(Stream stream, EncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        ThrowIfDisposed();
        CodecRegistry.Encode(this, stream, options);
    }

    /// <summary>Encodes the image and writes it to a file asynchronously.</summary>
    public async Task SaveAsync(string path, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        byte[] data = Encode(options);
        await File.WriteAllBytesAsync(path, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Encodes the image and writes it to a stream asynchronously.</summary>
    public async Task SaveAsync(Stream stream, EncoderOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        byte[] data = Encode(options);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Saves the image as WebP.</summary>
    /// <param name="path">Output file.</param>
    /// <param name="quality">0–100; for lossless, the compression effort.</param>
    /// <param name="lossless">Use lossless compression.</param>
    public void SaveAsWebP(string path, float quality = 75f, bool lossless = false) =>
        Save(path, new WebPEncoderOptions { Quality = quality, Lossless = lossless });

    /// <summary>Saves the image as PNG.</summary>
    public void SaveAsPng(string path) => Save(path, new PngEncoderOptions());

    /// <summary>Saves the image as BMP.</summary>
    public void SaveAsBmp(string path) => Save(path, new BmpEncoderOptions());

    /// <summary>Encodes the image as WebP.</summary>
    public byte[] ToWebP(WebPEncoderOptions? options = null) => Encode(options ?? new WebPEncoderOptions());

    /// <summary>Encodes the image as PNG.</summary>
    public byte[] ToPng(PngEncoderOptions? options = null) => Encode(options ?? new PngEncoderOptions());

    /// <summary>Encodes the image as BMP.</summary>
    public byte[] ToBmp(BmpEncoderOptions? options = null) => Encode(options ?? new BmpEncoderOptions());

    private byte[] Encode(EncoderOptions options)
    {
        using var ms = new MemoryStream();
        Save(ms, options);
        return ms.ToArray();
    }

    private static EncoderOptions DefaultOptions(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format == ImageFormat.WebP)
        {
            return new WebPEncoderOptions();
        }

        if (format == ImageFormat.Png)
        {
            return new PngEncoderOptions();
        }

        if (format == ImageFormat.Bmp)
        {
            return new BmpEncoderOptions();
        }

        throw new NotSupportedException($"Saving images in {format.Name} format is not supported.");
    }

    // ---------------------------------------------------------------- lifetime & helpers

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
        _disposed ? "Image (disposed)" : $"Image {Width}x{Height}, {RawFormat.Name}, frames: {FrameCount}";

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void CheckRectangle(Rectangle rect, string paramName)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 ||
            rect.Right > Width || rect.Bottom > Height)
        {
            throw new ArgumentOutOfRangeException(paramName, rect, $"The rectangle must lie within the image bounds {Width}x{Height}.");
        }
    }

    private static int CheckBuffer(int length, int rowBytes, int height, int stride, string paramName)
    {
        if (stride == 0)
        {
            stride = rowBytes;
        }
        else if (stride < rowBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {rowBytes} bytes.");
        }

        long required = ((long)stride * (height - 1)) + rowBytes;
        if (length < required)
        {
            throw new ArgumentException($"The buffer is too small: {required} bytes required, {length} provided.", paramName);
        }

        return stride;
    }
}

internal static class StreamHelper
{
    public static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment))
        {
            byte[] copy = segment.AsSpan((int)ms.Position).ToArray();
            ms.Position = ms.Length;
            return copy;
        }

        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, Math.Min(int.MaxValue, stream.Length - stream.Position)) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
