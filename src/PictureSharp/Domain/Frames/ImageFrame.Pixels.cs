using System.Buffers;
using PictureSharp.Domain.Pixels;

namespace PictureSharp;

public sealed partial class ImageFrame
{
    /// <summary>
    /// Returns a writable view of the pixels without a copy.
    /// An operation replacing the pixel buffer detaches this view from the frame.
    /// </summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public Memory<TPixel> GetPixelMemory<TPixel>() where TPixel : unmanaged => _pixels.As<TPixel>();

    /// <summary>Overwrites the frame with pixels of its own <see cref="PixelType"/>. Extra source pixels are ignored.</summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public void CopyPixelDataFrom<TPixel>(ReadOnlySpan<TPixel> source) where TPixel : unmanaged
    {
        TPixel[] pixels = _pixels.As<TPixel>();
        CheckPixelCount(source.Length, nameof(source));
        source[..pixels.Length].CopyTo(pixels);
    }

    /// <summary>Copies the pixels of the frame into <paramref name="destination"/>. Extra destination pixels are left untouched.</summary>
    /// <inheritdoc cref="GetPixel{TPixel}(int, int)"/>
    public void CopyPixelDataTo<TPixel>(Span<TPixel> destination) where TPixel : unmanaged
    {
        TPixel[] pixels = _pixels.As<TPixel>();
        CheckPixelCount(destination.Length, nameof(destination));
        pixels.CopyTo(destination);
    }

    /// <summary>
    /// Copies the frame into <paramref name="destination"/> in the given format, converting the sample depth as
    /// needed: 16-bit pixels are rounded to 8 bits for an 8-bit format after premultiplication or luma.
    /// </summary>
    /// <param name="destination">Target buffer.</param>
    /// <param name="format">Layout to write.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataTo(Span<byte> destination, PixelFormat format, int stride = 0)
    {
        int rowBytes = RowBytes(format);
        stride = CheckBuffer(destination.Length, rowBytes, Height, stride, nameof(destination));
        Rgba64[]? scratch = _pixels.Rgba32 is not null && PixelConverter.IsWide(format) ? ArrayPool<Rgba64>.Shared.Rent(Width) : null;
        try
        {
            for (int y = 0; y < Height; y++)
            {
                Span<byte> row = destination.Slice(y * stride, rowBytes);
                if (_pixels.Rgba32 is { } narrow)
                {
                    PixelConverter.FromPixels<Rgba32>(narrow.AsSpan(y * Width, Width), row, format, scratch);
                }
                else
                {
                    PixelConverter.FromPixels<Rgba64>(_pixels.Rgba64.AsSpan(y * Width, Width), row, format, default);
                }
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<Rgba64>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Overwrites the frame with pixels from <paramref name="source"/>, converting the sample depth to
    /// <see cref="PixelType"/>: a 16-bit format is rounded to 8-bit storage after unpremultiplication, and an 8-bit
    /// format is widened exactly to 16-bit storage.
    /// </summary>
    /// <param name="source">Source buffer.</param>
    /// <param name="format">Layout of <paramref name="source"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public void CopyPixelDataFrom(ReadOnlySpan<byte> source, PixelFormat format, int stride = 0)
    {
        int rowBytes = RowBytes(format);
        stride = CheckBuffer(source.Length, rowBytes, Height, stride, nameof(source));
        bool convert = PixelConverter.IsWide(format) != (PixelType == PixelType.Rgba64);
        Rgba64[]? scratch = convert ? ArrayPool<Rgba64>.Shared.Rent(Width) : null;
        try
        {
            for (int y = 0; y < Height; y++)
            {
                ReadOnlySpan<byte> row = source.Slice(y * stride, rowBytes);
                if (_pixels.Rgba32 is { } narrow)
                {
                    PixelConverter.ToPixels<Rgba32>(row, narrow.AsSpan(y * Width, Width), format, scratch);
                }
                else
                {
                    PixelConverter.ToPixels<Rgba64>(row, _pixels.Rgba64.AsSpan(y * Width, Width), format, scratch);
                }
            }
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<Rgba64>.Shared.Return(scratch);
            }
        }
    }

    /// <summary>
    /// Returns the frame as a tightly packed array in the given format, e.g. <see cref="PixelFormat.Bgra32"/>
    /// for WPF <c>BitmapSource.Create</c> or SkiaSharp <c>SKColorType.Bgra8888</c>.
    /// </summary>
    public byte[] ToPixelData(PixelFormat format = PixelFormat.Rgba32)
    {
        long length = (long)RowBytes(format) * Height;
        if (length > Array.MaxLength)
        {
            throw new InvalidOperationException($"The frame does not fit in one array as {format}.");
        }

        var result = GC.AllocateUninitializedArray<byte>((int)length);
        CopyPixelDataTo(result, format);
        return result;
    }

    /// <summary>Returns true when every pixel of this frame has full alpha (255 or 65535).</summary>
    public bool IsOpaque()
    {
        if (_pixels.Rgba32 is { } narrow)
        {
            foreach (Rgba32 pixel in narrow)
            {
                if (pixel.A != 255)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (Rgba64 pixel in _pixels.Rgba64!)
        {
            if (pixel.A != ushort.MaxValue)
            {
                return false;
            }
        }

        return true;
    }

    private int RowBytes(PixelFormat format)
    {
        long rowBytes = (long)Width * format.BytesPerPixel();
        return rowBytes <= Array.MaxLength
            ? (int)rowBytes
            : throw new ArgumentOutOfRangeException(nameof(format), format, "A row of the frame is too large for this format.");
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
