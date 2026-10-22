using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PictureSharp.Algorithms.Processing;

internal static partial class Affine
{
    /// <summary>
    /// <see cref="BicubicFilter32"/> with the four channels in the lanes of one vector: the same double operations in
    /// the same order, so the result is identical.
    /// </summary>
    private static void BicubicFilter32Simd(Span<byte> output, ReadOnlySpan<byte> image, int xsize, int ysize, double xin, double yin)
    {
        if (xin < 0.0 || xin >= xsize || yin < 0.0 || yin >= ysize)
        {
            return;
        }

        xin -= 0.5;
        yin -= 0.5;
        int x = Floor(xin);
        int y = Floor(yin);
        double dx = xin - x;
        double dy = yin - y;
        x--;
        y--;
        int stride = xsize * 4;
        int x0 = Clip(x, xsize) * 4;
        int x1 = Clip(x + 1, xsize) * 4;
        int x2 = Clip(x + 2, xsize) * 4;
        int x3 = Clip(x + 3, xsize) * 4;
        Vector256<double> d = Vector256.Create(dx);
        Vector256<double> v1 = Row(image.Slice(Clip(y, ysize) * stride), x0, x1, x2, x3, d);
        Vector256<double> v2 = y + 1 >= 0 && y + 1 < ysize ? Row(image.Slice((y + 1) * stride), x0, x1, x2, x3, d) : v1;
        Vector256<double> v3 = y + 2 >= 0 && y + 2 < ysize ? Row(image.Slice((y + 2) * stride), x0, x1, x2, x3, d) : v2;
        Vector256<double> v4 = y + 3 >= 0 && y + 3 < ysize ? Row(image.Slice((y + 3) * stride), x0, x1, x2, x3, d) : v3;
        Vector256<double> v = Bicubic(v1, v2, v3, v4, Vector256.Create(dy));
        for (int b = 0; b < 4; b++)
        {
            double c = v.GetElement(b);
            output[b] = c <= 0.0 ? (byte)0 : c >= 255.0 ? (byte)255 : (byte)(int)c;
        }
    }

    private static Vector256<double> Row(ReadOnlySpan<byte> row, int x0, int x1, int x2, int x3, Vector256<double> d) =>
        Bicubic(Pixel(row, x0), Pixel(row, x1), Pixel(row, x2), Pixel(row, x3), d);

    private static Vector256<double> Pixel(ReadOnlySpan<byte> row, int at)
    {
        if (Avx.IsSupported)
        {
            Vector128<byte> bytes = Vector128.CreateScalar(MemoryMarshal.Read<uint>(row.Slice(at, 4))).AsByte();
            return Avx.ConvertToVector256Double(Vector128.WidenLower(Vector128.WidenLower(bytes)).AsInt32());
        }

        return Vector256.Create(row[at], row[at + 1], (double)row[at + 2], row[at + 3]);
    }

    // port: Pillow src/libImaging/Geometry.c BICUBIC, lane by lane as Bicubic(double, ...) evaluates it.
    private static Vector256<double> Bicubic(Vector256<double> v1, Vector256<double> v2, Vector256<double> v3, Vector256<double> v4, Vector256<double> d)
    {
        Vector256<double> p1 = v2;
        Vector256<double> p2 = -v1 + v3;
        Vector256<double> p3 = (Vector256.Create(2.0) * (v1 - v2)) + v3 - v4;
        Vector256<double> p4 = -v1 + v2 - v3 + v4;
        return p1 + (d * (p2 + (d * (p3 + (d * p4)))));
    }
}
