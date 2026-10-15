using PictureSharp.Processing;

namespace PictureSharp;

public sealed partial class ImageFrame
{
    /// <summary>
    /// Returns a writable view of the RGBA pixels without a copy.
    /// An operation replacing the pixel buffer detaches this view from the frame.
    /// </summary>
    public Memory<Rgba32> GetPixelMemory() => _pixels;

    /// <summary>Overwrites the frame with RGBA pixels. Extra source pixels are ignored.</summary>
    public void CopyPixelDataFrom(ReadOnlySpan<Rgba32> source)
    {
        CheckPixelCount(source.Length, nameof(source));
        source[.._pixels.Length].CopyTo(_pixels);
    }

    /// <summary>Copies the pixels of the frame into <paramref name="destination"/>. Extra destination pixels are left untouched.</summary>
    public void CopyPixelDataTo(Span<Rgba32> destination)
    {
        CheckPixelCount(destination.Length, nameof(destination));
        _pixels.CopyTo(destination);
    }

    /// <summary>Copies the frame into <paramref name="destination"/> in the given format.</summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataTo(Span<byte> destination, PixelFormat format, int stride = 0)
    {
        int rowBytes = Width * format.BytesPerPixel();
        stride = CheckBuffer(destination.Length, rowBytes, Height, stride, nameof(destination));
        for (int y = 0; y < Height; y++)
        {
            PixelConverter.FromRgba(GetRowSpan(y), destination.Slice(y * stride, rowBytes), format);
        }
    }

    /// <summary>Overwrites the frame with pixels from <paramref name="source"/>.</summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataFrom(ReadOnlySpan<byte> source, PixelFormat format, int stride = 0)
    {
        int rowBytes = Width * format.BytesPerPixel();
        stride = CheckBuffer(source.Length, rowBytes, Height, stride, nameof(source));
        for (int y = 0; y < Height; y++)
        {
            PixelConverter.ToRgba(source.Slice(y * stride, rowBytes), GetRowSpan(y), format);
        }
    }

    /// <summary>
    /// Returns the frame as a tightly packed array in the given format, e.g. <see cref="PixelFormat.Bgra32"/>
    /// for WPF <c>BitmapSource.Create</c> or SkiaSharp <c>SKColorType.Bgra8888</c>.
    /// </summary>
    public byte[] ToPixelData(PixelFormat format = PixelFormat.Rgba32)
    {
        var result = GC.AllocateUninitializedArray<byte>(checked(Width * Height * format.BytesPerPixel()));
        CopyPixelDataTo(result, format);
        return result;
    }

    /// <summary>Returns true when every pixel of this frame has alpha 255.</summary>
    public bool IsOpaque()
    {
        foreach (Rgba32 pixel in _pixels)
        {
            if (pixel.A != 255)
            {
                return false;
            }
        }

        return true;
    }

    private void CheckPixelCount(int length, string paramName)
    {
        if (length < _pixels.Length)
        {
            throw new ArgumentException($"The buffer is too small: {_pixels.Length} pixels required, {length} provided.", paramName);
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
