using RasterSharp.Processing;

namespace RasterSharp;

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
    /// The memory stays valid until an operation changes the image size.
    /// </summary>
    public Memory<Rgba32> GetPixelMemory() => RootFrame.Pixels;

    /// <summary>Copies the pixels of the first frame into <paramref name="destination"/>.</summary>
    public void CopyPixelDataTo(Span<Rgba32> destination) => GetPixelSpan().CopyTo(destination);

    /// <summary>Copies the first frame into <paramref name="destination"/> in the given format.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataTo(Span<byte> destination, PixelFormat format, int stride = 0)
    {
        ImageFrame frame = RootFrame;
        int rowBytes = frame.Width * format.BytesPerPixel();
        stride = CheckBuffer(destination.Length, rowBytes, frame.Height, stride, nameof(destination));
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConverter.FromRgba(frame.GetRowSpan(y), destination.Slice(y * stride, rowBytes), format);
        }
    }

    /// <summary>Overwrites the first frame with pixels from <paramref name="source"/>.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataFrom(ReadOnlySpan<byte> source, PixelFormat format, int stride = 0)
    {
        ImageFrame frame = RootFrame;
        int rowBytes = frame.Width * format.BytesPerPixel();
        stride = CheckBuffer(source.Length, rowBytes, frame.Height, stride, nameof(source));
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConverter.ToRgba(source.Slice(y * stride, rowBytes), frame.GetRowSpan(y), format);
        }
    }

    /// <summary>
    /// Returns the first frame as a tightly packed array in the given format, e.g. <see cref="PixelFormat.Bgra32"/>
    /// for WPF <c>BitmapSource.Create</c> or SkiaSharp <c>SKColorType.Bgra8888</c>.
    /// </summary>
    public byte[] ToPixelData(PixelFormat format = PixelFormat.Rgba32)
    {
        var result = GC.AllocateUninitializedArray<byte>(checked(Width * Height * format.BytesPerPixel()));
        CopyPixelDataTo(result, format);
        return result;
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
