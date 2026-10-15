namespace PictureSharp;

public sealed partial class Image
{
    /// <summary>Gets or sets a pixel of the first frame. <see cref="System.Drawing.Color"/> values convert implicitly.</summary>
    public Rgba32 this[int x, int y]
    {
        get => RootFrame[x, y];
        set => RootFrame[x, y] = value;
    }

    /// <summary>Returns row <paramref name="y"/> of the first frame.</summary>
    public Span<Rgba32> GetRowSpan(int y) => RootFrame.GetRowSpan(y);

    /// <summary>Returns all pixels of the first frame, row by row without padding.</summary>
    public Span<Rgba32> GetPixelSpan() => RootFrame.GetPixelSpan();

    /// <summary>
    /// Returns all pixels of the first frame as memory, e.g. to <see cref="Memory{T}.Pin"/> them for native interop.
    /// The memory is a writable view without a copy. An operation replacing the pixel buffer detaches this view from the image.
    /// </summary>
    public Memory<Rgba32> GetPixelMemory() => RootFrame.GetPixelMemory();

    /// <summary>
    /// Returns a writable view of the first frame's RGBA bytes without a copy.
    /// An operation replacing the pixel buffer detaches this view from the image.
    /// </summary>
    public Span<byte> GetPixelByteSpan() => RootFrame.GetPixelByteSpan();

    /// <summary>Copies the pixels of the first frame into <paramref name="destination"/>.</summary>
    public void CopyPixelDataTo(Span<Rgba32> destination) => RootFrame.CopyPixelDataTo(destination);

    /// <summary>Copies the first frame into <paramref name="destination"/> in the given format.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataTo(Span<byte> destination, PixelFormat format, int stride = 0) =>
        RootFrame.CopyPixelDataTo(destination, format, stride);

    /// <summary>Overwrites the first frame with RGBA pixels and sets <see cref="HasAlpha"/>. Extra source pixels are ignored.</summary>
    public void CopyPixelDataFrom(ReadOnlySpan<Rgba32> source)
    {
        RootFrame.CopyPixelDataFrom(source);
        HasAlpha = true;
    }

    /// <summary>Overwrites the first frame with pixels from <paramref name="source"/>.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    /// <remarks>A format with alpha sets <see cref="HasAlpha"/>, as <see cref="LoadPixelData(ReadOnlySpan{byte}, int, int, PixelFormat, int)"/> does.</remarks>
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

    /// <summary>Returns true when every pixel of every frame has alpha 255.</summary>
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
