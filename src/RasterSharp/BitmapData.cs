using System.Drawing;
using System.Runtime.InteropServices;
using RasterSharp.Processing;

namespace RasterSharp;

/// <summary>
/// A locked region of an image converted to a requested <see cref="PixelFormat"/>, like
/// System.Drawing.Imaging.BitmapData. The buffer is pinned, so <see cref="Scan0"/> can be passed to
/// native code. Disposing (or <see cref="Image.UnlockBits"/>) writes changes back unless the lock is read-only.
/// </summary>
public sealed class BitmapData : IDisposable
{
    private readonly ImageFrame _frame;
    private byte[]? _buffer;

    internal BitmapData(ImageFrame frame, Rectangle rect, ImageLockMode mode, PixelFormat format)
    {
        _frame = frame;
        Rect = rect;
        LockMode = mode;
        PixelFormat = format;
        Stride = ((rect.Width * format.BytesPerPixel()) + 3) & ~3;
        _buffer = GC.AllocateArray<byte>(checked(Stride * rect.Height), pinned: true);

        if ((mode & ImageLockMode.ReadOnly) != 0)
        {
            for (int y = 0; y < rect.Height; y++)
            {
                PixelConverter.FromRgba(
                    frame.GetRowSpan(rect.Y + y).Slice(rect.X, rect.Width),
                    _buffer.AsSpan(y * Stride, Stride),
                    format);
            }
        }
    }

    /// <summary>Width of the locked region.</summary>
    public int Width => Rect.Width;

    /// <summary>Height of the locked region.</summary>
    public int Height => Rect.Height;

    /// <summary>The locked region in image coordinates.</summary>
    public Rectangle Rect { get; }

    /// <summary>Bytes per row (a multiple of 4).</summary>
    public int Stride { get; }

    /// <summary>Layout of the buffer.</summary>
    public PixelFormat PixelFormat { get; }

    /// <summary>Access mode of the lock.</summary>
    public ImageLockMode LockMode { get; }

    /// <summary>The whole buffer (<see cref="Stride"/> × <see cref="Height"/> bytes).</summary>
    public Span<byte> Span => Buffer;

    /// <summary>Address of the first byte of the pinned buffer.</summary>
    public IntPtr Scan0 => Marshal.UnsafeAddrOfPinnedArrayElement(Buffer, 0);

    /// <summary>Whether the data has been unlocked.</summary>
    public bool IsUnlocked => _buffer is null;

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(BitmapData), "The bits have been unlocked.");

    /// <summary>Returns the bytes of row <paramref name="y"/> (without padding).</summary>
    public Span<byte> GetRowSpan(int y)
    {
        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        return Buffer.AsSpan(y * Stride, Width * PixelFormat.BytesPerPixel());
    }

    /// <summary>Writes changes back (unless read-only) and releases the buffer.</summary>
    public void Dispose()
    {
        byte[]? buffer = _buffer;
        if (buffer is null)
        {
            return;
        }

        _buffer = null;
        if ((LockMode & ImageLockMode.WriteOnly) != 0)
        {
            for (int y = 0; y < Rect.Height; y++)
            {
                PixelConverter.ToRgba(
                    buffer.AsSpan(y * Stride, Stride),
                    _frame.GetRowSpan(Rect.Y + y).Slice(Rect.X, Rect.Width),
                    PixelFormat);
            }
        }
    }
}
