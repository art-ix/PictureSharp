using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PictureSharp.Domain.Pixels;

/// <summary>
/// Conversions involving 16-bit storage or the 16-bit layouts. Values are converted at the higher of the two
/// precisions: 16-bit pixels are premultiplied or reduced to luma before rounding to an 8-bit layout, 16-bit layouts
/// are unpremultiplied before rounding to 8-bit storage, and 8-bit layouts are read at 8 bits and then widened exactly.
/// </summary>
internal static partial class PixelConverter
{
    /// <summary>Whether <paramref name="format"/> has 16 bits per channel.</summary>
    public static bool IsWide(PixelFormat format) =>
        format is PixelFormat.Rgba64 or PixelFormat.Rgba64Premultiplied or PixelFormat.Rgb48 or PixelFormat.Gray16;

    /// <summary>BT.601 luma of 16-bit samples, with the weights of <see cref="Luma"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Luma16(ushort r, ushort g, ushort b) => (ushort)(((19595u * r) + (38470u * g) + (7471u * b) + 32768u) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Premultiply16(ushort c, ushort a) => (ushort)((((uint)c * a) + 32767u) / 65535u);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort Unpremultiply16(ushort c, ushort a)
    {
        if (a == 0)
        {
            return 0;
        }

        uint v = (((uint)c * 65535u) + (uint)(a >> 1)) / a;
        return v > 65535u ? ushort.MaxValue : (ushort)v;
    }

    /// <summary>
    /// Converts one row of pixels of storage <typeparamref name="TPixel"/> to <paramref name="format"/>;
    /// <paramref name="scratch"/> holds at least <paramref name="src"/>.Length pixels for a 16-bit layout of 8-bit storage.
    /// </summary>
    public static void FromPixels<TPixel>(ReadOnlySpan<TPixel> src, Span<byte> dst, PixelFormat format, Span<Rgba64> scratch)
        where TPixel : unmanaged
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            ReadOnlySpan<Rgba32> pixels = MemoryMarshal.Cast<TPixel, Rgba32>(src);
            if (!IsWide(format))
            {
                FromRgba(pixels, dst, format);
                return;
            }

            Span<Rgba64> wide = scratch[..pixels.Length];
            PixelDepth.Widen(pixels, wide);
            FromRgba64(wide, dst, format);
        }
        else
        {
            FromRgba64(MemoryMarshal.Cast<TPixel, Rgba64>(src), dst, format);
        }
    }

    /// <summary>
    /// Converts one row from <paramref name="format"/> to pixels of storage <typeparamref name="TPixel"/>;
    /// <paramref name="scratch"/> holds at least <paramref name="dst"/>.Length pixels when the depths differ.
    /// </summary>
    public static void ToPixels<TPixel>(ReadOnlySpan<byte> src, Span<TPixel> dst, PixelFormat format, Span<Rgba64> scratch)
        where TPixel : unmanaged
    {
        if (typeof(TPixel) == typeof(Rgba32))
        {
            Span<Rgba32> pixels = MemoryMarshal.Cast<TPixel, Rgba32>(dst);
            if (!IsWide(format))
            {
                ToRgba(src, pixels, format);
                return;
            }

            Span<Rgba64> wide = scratch[..pixels.Length];
            ToRgba64(src, wide, format);
            PixelDepth.Narrow(wide, pixels);
        }
        else
        {
            Span<Rgba64> pixels = MemoryMarshal.Cast<TPixel, Rgba64>(dst);
            if (IsWide(format))
            {
                ToRgba64(src, pixels, format);
                return;
            }

            // 8-bit layouts convert at their own precision, so a later reduction gives the 8-bit storage result.
            Span<Rgba32> narrow = MemoryMarshal.Cast<Rgba64, Rgba32>(scratch)[..pixels.Length];
            ToRgba(src, narrow, format);
            PixelDepth.Widen(narrow, pixels);
        }
    }

    /// <summary>Converts one row of a 16-bit layout to <see cref="Rgba64"/>.</summary>
    public static void ToRgba64(ReadOnlySpan<byte> src, Span<Rgba64> dst, PixelFormat format)
    {
        int n = dst.Length;
        switch (format)
        {
            case PixelFormat.Rgba64 when BitConverter.IsLittleEndian:
                src[..(n * 8)].CopyTo(MemoryMarshal.AsBytes(dst));
                break;
            case PixelFormat.Rgba64:
                for (int i = 0, j = 0; i < n; i++, j += 8)
                {
                    dst[i] = new Rgba64(Read(src, j), Read(src, j + 2), Read(src, j + 4), Read(src, j + 6));
                }

                break;
            case PixelFormat.Rgba64Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 8)
                {
                    ushort a = Read(src, j + 6);
                    dst[i] = new Rgba64(Unpremultiply16(Read(src, j), a), Unpremultiply16(Read(src, j + 2), a), Unpremultiply16(Read(src, j + 4), a), a);
                }

                break;
            case PixelFormat.Rgb48:
                for (int i = 0, j = 0; i < n; i++, j += 6)
                {
                    dst[i] = new Rgba64(Read(src, j), Read(src, j + 2), Read(src, j + 4));
                }

                break;
            case PixelFormat.Gray16:
                for (int i = 0; i < n; i++)
                {
                    ushort v = Read(src, 2 * i);
                    dst[i] = new Rgba64(v, v, v);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Not a 16-bit pixel format.");
        }
    }

    /// <summary>Converts one row of <see cref="Rgba64"/> to any layout, rounding to 8 bits after the 16-bit conversion.</summary>
    public static void FromRgba64(ReadOnlySpan<Rgba64> src, Span<byte> dst, PixelFormat format)
    {
        int n = src.Length;
        switch (format)
        {
            case PixelFormat.Rgba64 when BitConverter.IsLittleEndian:
                MemoryMarshal.AsBytes(src).CopyTo(dst);
                break;
            case PixelFormat.Rgba64:
                for (int i = 0, j = 0; i < n; i++, j += 8)
                {
                    Rgba64 p = src[i];
                    Write(dst, j, p.R);
                    Write(dst, j + 2, p.G);
                    Write(dst, j + 4, p.B);
                    Write(dst, j + 6, p.A);
                }

                break;
            case PixelFormat.Rgba64Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 8)
                {
                    Rgba64 p = src[i];
                    Write(dst, j, Premultiply16(p.R, p.A));
                    Write(dst, j + 2, Premultiply16(p.G, p.A));
                    Write(dst, j + 4, Premultiply16(p.B, p.A));
                    Write(dst, j + 6, p.A);
                }

                break;
            case PixelFormat.Rgb48:
                for (int i = 0, j = 0; i < n; i++, j += 6)
                {
                    Rgba64 p = src[i];
                    Write(dst, j, p.R);
                    Write(dst, j + 2, p.G);
                    Write(dst, j + 4, p.B);
                }

                break;
            case PixelFormat.Gray16:
                for (int i = 0; i < n; i++)
                {
                    Rgba64 p = src[i];
                    Write(dst, 2 * i, Luma16(p.R, p.G, p.B));
                }

                break;
            case PixelFormat.Rgba32:
                PixelDepth.Narrow(src, MemoryMarshal.Cast<byte, Rgba32>(dst[..(n * 4)]));
                break;
            case PixelFormat.Bgra32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(p.B);
                    dst[j + 1] = Rgba64.Narrow(p.G);
                    dst[j + 2] = Rgba64.Narrow(p.R);
                    dst[j + 3] = Rgba64.Narrow(p.A);
                }

                break;
            case PixelFormat.Argb32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(p.A);
                    dst[j + 1] = Rgba64.Narrow(p.R);
                    dst[j + 2] = Rgba64.Narrow(p.G);
                    dst[j + 3] = Rgba64.Narrow(p.B);
                }

                break;
            case PixelFormat.Rgb24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(p.R);
                    dst[j + 1] = Rgba64.Narrow(p.G);
                    dst[j + 2] = Rgba64.Narrow(p.B);
                }

                break;
            case PixelFormat.Bgr24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(p.B);
                    dst[j + 1] = Rgba64.Narrow(p.G);
                    dst[j + 2] = Rgba64.Narrow(p.R);
                }

                break;
            case PixelFormat.Gray8:
                for (int i = 0; i < n; i++)
                {
                    Rgba64 p = src[i];
                    dst[i] = Rgba64.Narrow(Luma16(p.R, p.G, p.B));
                }

                break;
            case PixelFormat.Rgba32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(Premultiply16(p.R, p.A));
                    dst[j + 1] = Rgba64.Narrow(Premultiply16(p.G, p.A));
                    dst[j + 2] = Rgba64.Narrow(Premultiply16(p.B, p.A));
                    dst[j + 3] = Rgba64.Narrow(p.A);
                }

                break;
            case PixelFormat.Bgra32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba64 p = src[i];
                    dst[j] = Rgba64.Narrow(Premultiply16(p.B, p.A));
                    dst[j + 1] = Rgba64.Narrow(Premultiply16(p.G, p.A));
                    dst[j + 2] = Rgba64.Narrow(Premultiply16(p.R, p.A));
                    dst[j + 3] = Rgba64.Narrow(p.A);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format.");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Read(ReadOnlySpan<byte> src, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(src[offset..]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Write(Span<byte> dst, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(dst[offset..], value);
}
