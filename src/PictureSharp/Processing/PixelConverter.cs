using System.Runtime.CompilerServices;

namespace PictureSharp.Processing;

/// <summary>Row-wise conversions between <see cref="Rgba32"/> and the external <see cref="PixelFormat"/> layouts.</summary>
internal static class PixelConverter
{
    /// <summary>BT.601 luma in 16.16 fixed point (weights sum to 65536).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Luma(byte r, byte g, byte b) => (byte)(((19595 * r) + (38470 * g) + (7471 * b) + 32768) >> 16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Premultiply(byte c, byte a) => (byte)(((c * a) + 127) / 255);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte Unpremultiply(byte c, byte a)
    {
        if (a == 0)
        {
            return 0;
        }

        int v = ((c * 255) + (a >> 1)) / a;
        return v > 255 ? (byte)255 : (byte)v;
    }

    /// <summary>Converts one row of <paramref name="dst"/>.Length pixels from <paramref name="format"/> to RGBA.</summary>
    public static void ToRgba(ReadOnlySpan<byte> src, Span<Rgba32> dst, PixelFormat format)
    {
        int n = dst.Length;
        switch (format)
        {
            case PixelFormat.Rgba32:
                src[..(n * 4)].CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dst));
                break;
            case PixelFormat.Bgra32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    dst[i] = new Rgba32(src[j + 2], src[j + 1], src[j], src[j + 3]);
                }

                break;
            case PixelFormat.Argb32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    dst[i] = new Rgba32(src[j + 1], src[j + 2], src[j + 3], src[j]);
                }

                break;
            case PixelFormat.Rgb24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    dst[i] = new Rgba32(src[j], src[j + 1], src[j + 2]);
                }

                break;
            case PixelFormat.Bgr24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    dst[i] = new Rgba32(src[j + 2], src[j + 1], src[j]);
                }

                break;
            case PixelFormat.Gray8:
                for (int i = 0; i < n; i++)
                {
                    byte v = src[i];
                    dst[i] = new Rgba32(v, v, v);
                }

                break;
            case PixelFormat.Rgba32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    byte a = src[j + 3];
                    dst[i] = new Rgba32(Unpremultiply(src[j], a), Unpremultiply(src[j + 1], a), Unpremultiply(src[j + 2], a), a);
                }

                break;
            case PixelFormat.Bgra32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    byte a = src[j + 3];
                    dst[i] = new Rgba32(Unpremultiply(src[j + 2], a), Unpremultiply(src[j + 1], a), Unpremultiply(src[j], a), a);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format.");
        }
    }

    /// <summary>Converts one row of <paramref name="src"/>.Length pixels from RGBA to <paramref name="format"/>.</summary>
    public static void FromRgba(ReadOnlySpan<Rgba32> src, Span<byte> dst, PixelFormat format)
    {
        int n = src.Length;
        switch (format)
        {
            case PixelFormat.Rgba32:
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(src).CopyTo(dst);
                break;
            case PixelFormat.Bgra32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba32 p = src[i];
                    dst[j] = p.B;
                    dst[j + 1] = p.G;
                    dst[j + 2] = p.R;
                    dst[j + 3] = p.A;
                }

                break;
            case PixelFormat.Argb32:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba32 p = src[i];
                    dst[j] = p.A;
                    dst[j + 1] = p.R;
                    dst[j + 2] = p.G;
                    dst[j + 3] = p.B;
                }

                break;
            case PixelFormat.Rgb24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    Rgba32 p = src[i];
                    dst[j] = p.R;
                    dst[j + 1] = p.G;
                    dst[j + 2] = p.B;
                }

                break;
            case PixelFormat.Bgr24:
                for (int i = 0, j = 0; i < n; i++, j += 3)
                {
                    Rgba32 p = src[i];
                    dst[j] = p.B;
                    dst[j + 1] = p.G;
                    dst[j + 2] = p.R;
                }

                break;
            case PixelFormat.Gray8:
                for (int i = 0; i < n; i++)
                {
                    Rgba32 p = src[i];
                    dst[i] = Luma(p.R, p.G, p.B);
                }

                break;
            case PixelFormat.Rgba32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba32 p = src[i];
                    dst[j] = Premultiply(p.R, p.A);
                    dst[j + 1] = Premultiply(p.G, p.A);
                    dst[j + 2] = Premultiply(p.B, p.A);
                    dst[j + 3] = p.A;
                }

                break;
            case PixelFormat.Bgra32Premultiplied:
                for (int i = 0, j = 0; i < n; i++, j += 4)
                {
                    Rgba32 p = src[i];
                    dst[j] = Premultiply(p.B, p.A);
                    dst[j + 1] = Premultiply(p.G, p.A);
                    dst[j + 2] = Premultiply(p.R, p.A);
                    dst[j + 3] = p.A;
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown pixel format.");
        }
    }
}
