using System.Globalization;
using System.Runtime.InteropServices;

namespace RasterSharp.Processing;

/// <summary>Rotation by an arbitrary angle, ported from Pillow's <c>Image.rotate</c> and the affine transforms in Geometry.c.</summary>
internal static class Affine
{
    /// <summary>
    /// The inverse (destination → source) affine matrix and output size for rotating a
    /// <paramref name="width"/>×<paramref name="height"/> image counter-clockwise by <paramref name="angle"/> degrees.
    /// </summary>
    // port: Pillow src/PIL/Image.py Image.rotate (center None, translate None)
    public static (double[] Matrix, int Width, int Height) RotationMatrix(int width, int height, double angle, bool expand)
    {
        double w = width, h = height;
        double cx = w / 2.0, cy = h / 2.0;
        double radians = -(angle * (Math.PI / 180.0));
        double[] m =
        [
            PyRound15(Math.Cos(radians)), PyRound15(Math.Sin(radians)), 0.0,
            PyRound15(-Math.Sin(radians)), PyRound15(Math.Cos(radians)), 0.0,
        ];

        (m[2], m[5]) = Transform(-cx, -cy, m);
        m[2] += cx;
        m[5] += cy;
        if (!expand)
        {
            return (m, width, height);
        }

        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        foreach ((double x, double y) in new[] { (0.0, 0.0), (w, 0.0), (w, h), (0.0, h) })
        {
            (double tx, double ty) = Transform(x, y, m);
            (minX, maxX) = (Math.Min(minX, tx), Math.Max(maxX, tx));
            (minY, maxY) = (Math.Min(minY, ty), Math.Max(maxY, ty));
        }

        int nw = checked((int)(Math.Ceiling(maxX) - Math.Floor(minX)));
        int nh = checked((int)(Math.Ceiling(maxY) - Math.Floor(minY)));
        (m[2], m[5]) = Transform(-(nw - width) / 2.0, -(nh - height) / 2.0, m);
        return (m, nw, nh);
    }

    /// <summary>Nearest-neighbour affine transform of straight-alpha pixels; pixels mapped from outside the source keep their value.</summary>
    // port: Pillow src/libImaging/Geometry.c ImagingTransformAffine (nearest: affine_fixed, else the floating point loop)
    public static void TransformNearest(ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Span<Rgba32> dst, int width, int height, double[] a)
    {
        if (CheckFixed(a, 0, 0) && CheckFixed(a, width, height) && CheckFixed(a, 0, height) && CheckFixed(a, width, 0))
        {
            // use 16.16 fixed point arithmetics
            int a0 = Fix(a[0]), a1 = Fix(a[1]), a3 = Fix(a[3]), a4 = Fix(a[4]);
            int a2 = Fix(a[2] + (a[1] * 0.5) + (a[0] * 0.5));
            int a5 = Fix(a[5] + (a[4] * 0.5) + (a[3] * 0.5));
            for (int y = 0; y < height; y++)
            {
                Span<Rgba32> row = dst.Slice(y * width, width);
                int xx = a2, yy = a5;
                for (int x = 0; x < width; x++)
                {
                    int xin = xx >> 16;
                    if (xin >= 0 && xin < srcWidth)
                    {
                        int yin = yy >> 16;
                        if (yin >= 0 && yin < srcHeight)
                        {
                            row[x] = src[(yin * srcWidth) + xin];
                        }
                    }

                    xx += a0;
                    yy += a3;
                }

                a2 += a1;
                a5 += a4;
            }

            return;
        }

        double xo = a[2] + (a[1] * 0.5) + (a[0] * 0.5);
        double yo = a[5] + (a[4] * 0.5) + (a[3] * 0.5);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba32> row = dst.Slice(y * width, width);
            double xx = xo, yy = yo;
            for (int x = 0; x < width; x++)
            {
                int xin = Coord(xx);
                if (xin >= 0 && xin < srcWidth)
                {
                    int yin = Coord(yy);
                    if (yin >= 0 && yin < srcHeight)
                    {
                        row[x] = src[(yin * srcWidth) + xin];
                    }
                }

                xx += a[0];
                yy += a[3];
            }

            xo += a[1];
            yo += a[4];
        }
    }

    /// <summary>Bilinear or bicubic affine transform of premultiplied pixels; pixels mapped from outside the source keep their value.</summary>
    // port: Pillow src/libImaging/Geometry.c ImagingGenericTransform with affine_transform
    public static void TransformFiltered(ReadOnlySpan<Rgba32> src, int srcWidth, int srcHeight, Span<Rgba32> dst, int width, int height,
        double[] a, ResamplingFilter filter)
    {
        ReadOnlySpan<byte> image = MemoryMarshal.AsBytes(src);
        Span<byte> output = MemoryMarshal.AsBytes(dst);
        bool bicubic = filter == ResamplingFilter.Bicubic;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // port: Geometry.c affine_transform
                double xin = x + 0.5;
                double yin = y + 0.5;
                double xx = (a[0] * xin) + (a[1] * yin) + a[2];
                double yy = (a[3] * xin) + (a[4] * yin) + a[5];
                Span<byte> pixel = output.Slice(((y * width) + x) * 4, 4);
                if (bicubic)
                {
                    BicubicFilter32(pixel, image, srcWidth, srcHeight, xx, yy);
                }
                else
                {
                    BilinearFilter32(pixel, image, srcWidth, srcHeight, xx, yy);
                }
            }
        }
    }

    // port: Pillow src/libImaging/Geometry.c bilinear_filter32RGB (BILINEAR_HEAD, BILINEAR_BODY)
    private static void BilinearFilter32(Span<byte> output, ReadOnlySpan<byte> image, int xsize, int ysize, double xin, double yin)
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
        int stride = xsize * 4;
        int x0 = Clip(x, xsize) * 4;
        int x1 = Clip(x + 1, xsize) * 4;
        for (int b = 0; b < 4; b++)
        {
            ReadOnlySpan<byte> row = image.Slice(Clip(y, ysize) * stride);
            double v1 = Bilinear(row[x0 + b], row[x1 + b], dx);
            double v2;
            if (y + 1 >= 0 && y + 1 < ysize)
            {
                row = image.Slice((y + 1) * stride);
                v2 = Bilinear(row[x0 + b], row[x1 + b], dx);
            }
            else
            {
                v2 = v1;
            }

            v1 = Bilinear(v1, v2, dy);
            output[b] = (byte)(int)v1;
        }
    }

    // port: Pillow src/libImaging/Geometry.c bicubic_filter32RGB (BICUBIC_HEAD, BICUBIC_BODY)
    private static void BicubicFilter32(Span<byte> output, ReadOnlySpan<byte> image, int xsize, int ysize, double xin, double yin)
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
        for (int b = 0; b < 4; b++)
        {
            ReadOnlySpan<byte> row = image.Slice(Clip(y, ysize) * stride);
            double v1 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            double v2 = v1, v3, v4;
            if (y + 1 >= 0 && y + 1 < ysize)
            {
                row = image.Slice((y + 1) * stride);
                v2 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v3 = v2;
            if (y + 2 >= 0 && y + 2 < ysize)
            {
                row = image.Slice((y + 2) * stride);
                v3 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v4 = v3;
            if (y + 3 >= 0 && y + 3 < ysize)
            {
                row = image.Slice((y + 3) * stride);
                v4 = Bicubic(row[x0 + b], row[x1 + b], row[x2 + b], row[x3 + b], dx);
            }

            v1 = Bicubic(v1, v2, v3, v4, dy);
            output[b] = v1 <= 0.0 ? (byte)0 : v1 >= 255.0 ? (byte)255 : (byte)(int)v1;
        }
    }

    // port: Pillow src/libImaging/Geometry.c BILINEAR
    private static double Bilinear(double a, double b, double d) => a + ((b - a) * d);

    // port: Pillow src/libImaging/Geometry.c BICUBIC
    private static double Bicubic(double v1, double v2, double v3, double v4, double d)
    {
        double p1 = v2;
        double p2 = -v1 + v3;
        double p3 = (2 * (v1 - v2)) + v3 - v4;
        double p4 = -v1 + v2 - v3 + v4;
        return p1 + (d * (p2 + (d * (p3 + (d * p4)))));
    }

    private static (double X, double Y) Transform(double x, double y, double[] m) =>
        ((m[0] * x) + (m[1] * y) + m[2], (m[3] * x) + (m[4] * y) + m[5]);

    // Python round(v, 15): the correctly rounded 15-decimal value.
    private static double PyRound15(double v) =>
        double.Parse(v.ToString("F15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    // port: Pillow src/libImaging/Geometry.c check_fixed
    private static bool CheckFixed(double[] a, int x, int y) =>
        Math.Abs((x * a[0]) + (y * a[1]) + a[2]) < 32768.0 && Math.Abs((x * a[3]) + (y * a[4]) + a[5]) < 32768.0;

    // port: Pillow src/libImaging/Geometry.c FIX, FLOOR, COORD, XCLIP/YCLIP
    private static int Fix(double v) => Floor((v * 65536.0) + 0.5);

    private static int Floor(double x) => x < 0.0 ? (int)Math.Floor(x) : (int)x;

    private static int Coord(double v) => v < 0.0 ? -1 : (int)v;

    private static int Clip(int v, int size) => v < 0 ? 0 : v < size ? v : size - 1;
}
