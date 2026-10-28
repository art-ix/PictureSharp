namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>
    /// Gets or sets a pixel of the first frame of an 8-bit image. <see cref="System.Drawing.Color"/> values convert
    /// implicitly. Use <see cref="GetPixel{TPixel}"/> and <see cref="SetPixel{TPixel}"/> for any <see cref="PixelType"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The image stores <see cref="Rgba64"/> pixels.</exception>
    public Rgba32 this[int x, int y]
    {
        get => RootFrame[x, y];
        set => RootFrame[x, y] = value;
    }

    /// <summary>The 8-bit pixels of the first frame for algorithms without a 16-bit implementation yet.</summary>
    /// <exception cref="NotSupportedException">The image stores <see cref="Rgba64"/> pixels.</exception>
    internal Rgba32[] Pixels => RootFrame.Pixels;

    /// <summary>Row <paramref name="y"/> of <see cref="Pixels"/>.</summary>
    internal Span<Rgba32> PixelRow(int y) => RootFrame.PixelRow(y);

    /// <summary>Returns the pixel at (<paramref name="x"/>, <paramref name="y"/>) of the first frame.</summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public TPixel GetPixel<TPixel>(int x, int y) where TPixel : unmanaged => RootFrame.GetPixel<TPixel>(x, y);

    /// <summary>Sets the pixel at (<paramref name="x"/>, <paramref name="y"/>) of the first frame.</summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public void SetPixel<TPixel>(int x, int y, TPixel value) where TPixel : unmanaged => RootFrame.SetPixel(x, y, value);

    /// <summary>Returns row <paramref name="y"/> of the first frame.</summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public Span<TPixel> GetRowSpan<TPixel>(int y) where TPixel : unmanaged => RootFrame.GetRowSpan<TPixel>(y);

    /// <summary>
    /// Returns all pixels of the first frame, row by row without padding. An operation replacing the pixel buffer
    /// (including a <see cref="PixelType"/> conversion) detaches this view from the image.
    /// </summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public Span<TPixel> GetPixelSpan<TPixel>() where TPixel : unmanaged => RootFrame.GetPixelSpan<TPixel>();

    /// <summary>
    /// Returns all pixels of the first frame as memory, e.g. to <see cref="Memory{T}.Pin"/> them for native interop.
    /// The memory is a writable view without a copy. An operation replacing the pixel buffer detaches this view from the image.
    /// </summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public Memory<TPixel> GetPixelMemory<TPixel>() where TPixel : unmanaged => RootFrame.GetPixelMemory<TPixel>();

    /// <summary>
    /// Returns a writable view of the first frame's pixel bytes in the native layout of <see cref="PixelType"/>,
    /// without a copy. An operation replacing the pixel buffer detaches this view from the image.
    /// </summary>
    public Span<byte> GetPixelByteSpan() => RootFrame.GetPixelByteSpan();

    /// <summary>Copies the pixels of the first frame into <paramref name="destination"/>.</summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public void CopyPixelDataTo<TPixel>(Span<TPixel> destination) where TPixel : unmanaged => RootFrame.CopyPixelDataTo(destination);

    /// <summary>Copies the first frame into <paramref name="destination"/> in the given format, converting the sample depth as needed.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataTo(Span<byte> destination, PixelFormat format, int stride = 0) =>
        RootFrame.CopyPixelDataTo(destination, format, stride);

    /// <summary>
    /// Overwrites the first frame with pixels of the image's <see cref="PixelType"/> and sets <see cref="HasAlpha"/>.
    /// Extra source pixels are ignored.
    /// </summary>
    /// <inheritdoc cref="ImageFrame.GetPixel{TPixel}(int, int)"/>
    public void CopyPixelDataFrom<TPixel>(ReadOnlySpan<TPixel> source) where TPixel : unmanaged
    {
        RootFrame.CopyPixelDataFrom(source);
        HasAlpha = true;
    }

    /// <summary>Overwrites the first frame with pixels from <paramref name="source"/>, converting to the image's <see cref="PixelType"/>.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    /// <remarks>A format with alpha sets <see cref="HasAlpha"/>, as <see cref="LoadPixelData(ReadOnlySpan{byte}, int, int, PixelFormat, int, PixelType)"/> does.</remarks>
    public void CopyPixelDataFrom(ReadOnlySpan<byte> source, PixelFormat format, int stride = 0)
    {
        RootFrame.CopyPixelDataFrom(source, format, stride);
        HasAlpha |= format.HasAlpha();
    }

    /// <summary>
    /// Returns the first frame as a tightly packed array in the given format, e.g. <see cref="PixelFormat.Bgra32"/>
    /// for WPF <c>BitmapSource.Create</c> or SkiaSharp <c>SKColorType.Bgra8888</c>.
    /// </summary>
    public byte[] ToPixelData(PixelFormat format = PixelFormat.Rgba32) => RootFrame.ToPixelData(format);

    /// <summary>Returns true when every pixel of every frame has full alpha (255 or 65535).</summary>
    public bool IsOpaque()
    {
        foreach (ImageFrame frame in Frames)
        {
            if (!frame.IsOpaque())
            {
                return false;
            }
        }

        return true;
    }
}
