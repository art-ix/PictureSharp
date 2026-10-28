using PictureSharp.Processing;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Independent double-precision references for the 16-bit kernels and composition of 0.9.0 step 2: direct
/// convolutions and closed formulas on whole images, without the library's banding, sliding sums, float storage or
/// transfer tables. Pixels are unit values (4 per pixel); results are rounded once, half up.
/// </summary>
internal static class WideReference
{
    public static double[] Premultiply(ReadOnlySpan<Rgba64> src, bool linear = false)
    {
        var result = new double[src.Length * 4];
        for (int i = 0; i < src.Length; i++)
        {
            double a = src[i].A / 65535.0;
            result[i * 4] = Channel(src[i].R, linear) * a;
            result[(i * 4) + 1] = Channel(src[i].G, linear) * a;
            result[(i * 4) + 2] = Channel(src[i].B, linear) * a;
            result[(i * 4) + 3] = a;
        }

        return result;
    }

    /// <summary>Straight-alpha pixels; encoded values clip every channel before dividing, linear ones divide by the raw alpha.</summary>
    public static Rgba64[] Unpremultiply(double[] v, bool linear = false)
    {
        var result = new Rgba64[v.Length / 4];
        for (int i = 0; i < result.Length; i++)
        {
            double a = Math.Clamp(v[(i * 4) + 3], 0, 1);
            ushort alpha = Round16(a);
            if (alpha == 0)
            {
                continue;
            }

            double divisor = linear ? v[(i * 4) + 3] : a;
            ushort C(int c)
            {
                double x = linear ? v[(i * 4) + c] : Math.Clamp(v[(i * 4) + c], 0, 1);
                double straight = x / divisor;
                return Round16(linear ? Encode(straight) : straight);
            }

            result[i] = new Rgba64(C(0), C(1), C(2), alpha);
        }

        return result;
    }

    public static double Decode(double v) => v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    public static double Encode(double v)
    {
        v = Math.Clamp(v, 0, 1);
        return v <= 0.0031308 ? v * 12.92 : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055;
    }

    public static ushort Round16(double unit) => (ushort)Math.Floor((Math.Clamp(unit, 0, 1) * 65535) + 0.5);

    public static byte Round8(double unit) => (byte)Math.Floor((Math.Clamp(unit, 0, 1) * 255) + 0.5);

    private static double Channel(ushort v, bool linear) => linear ? Decode(v / 65535.0) : v / 65535.0;

    // ------------------------------------------------------------ resampling (Pillow's Resample.c geometry)

    public static Rgba64[] Resize(Rgba64[] src, int srcWidth, int srcHeight, int width, int height, ResamplingFilter filter, bool linear)
    {
        double[] v = Premultiply(src, linear);
        if (width != srcWidth)
        {
            v = Pass(v, srcWidth, srcHeight, width, filter, horizontal: true, clip: !linear);
        }

        if (height != srcHeight)
        {
            v = Pass(v, width, srcHeight, height, filter, horizontal: false, clip: !linear);
        }

        return Unpremultiply(v, linear);
    }

    private static double[] Pass(double[] v, int w, int h, int outSize, ResamplingFilter filter, bool horizontal, bool clip)
    {
        int inSize = horizontal ? w : h;
        (int ow, int oh) = horizontal ? (outSize, h) : (w, outSize);
        var result = new double[ow * oh * 4];
        double support = filter switch
        {
            ResamplingFilter.Box => 0.5,
            ResamplingFilter.Bilinear => 1.0,
            ResamplingFilter.Bicubic => 2.0,
            _ => 3.0,
        };
        double scale = (double)inSize / outSize;
        double filterScale = Math.Max(1.0, scale);
        support *= filterScale;
        for (int o = 0; o < outSize; o++)
        {
            double center = (o + 0.5) * scale;
            int min = Math.Max(0, (int)(center - support + 0.5));
            int max = Math.Min(inSize, (int)(center + support + 0.5));
            var weights = new double[max - min];
            for (int i = min; i < max; i++)
            {
                weights[i - min] = Kernel(filter, (i - center + 0.5) / filterScale);
            }

            double total = weights.Sum();
            for (int line = 0; line < (horizontal ? h : w); line++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (int i = min; i < max; i++)
                    {
                        int index = horizontal ? (line * w) + i : (i * w) + line;
                        sum += v[(index * 4) + c] * weights[i - min] / total;
                    }

                    int target = horizontal ? (line * ow) + o : (o * ow) + line;
                    result[(target * 4) + c] = clip ? Math.Clamp(sum, 0, 1) : sum;
                }
            }
        }

        return result;
    }

    private static double Kernel(ResamplingFilter filter, double x)
    {
        if (filter == ResamplingFilter.Box)
        {
            return x > -0.5 && x <= 0.5 ? 1 : 0;
        }

        x = Math.Abs(x);
        switch (filter)
        {
            case ResamplingFilter.Bilinear:
                return x < 1 ? 1 - x : 0;
            case ResamplingFilter.Bicubic:
                const double a = -0.5;
                return x < 1 ? ((a + 2) * x * x * x) - ((a + 3) * x * x) + 1 : x < 2 ? a * ((x * x * x) - (5 * x * x) + (8 * x) - 4) : 0;
            default:
                return x < 3 ? Sinc(x) * Sinc(x / 3) : 0;
        }

        static double Sinc(double t) => t == 0 ? 1 : Math.Sin(Math.PI * t) / (Math.PI * t);
    }

    // ------------------------------------------------------------ box, Gaussian, unsharp mask, 3×3

    /// <summary>
    /// Pillow's box blur written as a direct convolution: weight 1/(2r+1) on the 2⌊r⌋+1 central taps and the rest
    /// split between the two next taps, with indices clamped to the edge.
    /// </summary>
    public static double[] BoxBlur(double[] v, int w, int h, double radius, int passes)
    {
        for (int p = 0; p < passes; p++)
        {
            v = BoxLine(v, w, h, radius, horizontal: true);
        }

        for (int p = 0; p < passes; p++)
        {
            v = BoxLine(v, w, h, radius, horizontal: false);
        }

        return v;
    }

    private static double[] BoxLine(double[] v, int w, int h, double radius, bool horizontal)
    {
        int r = (int)radius;
        double ww = 1 / ((2 * radius) + 1);
        double fw = (1 - (((2 * r) + 1) * ww)) / 2;
        int length = horizontal ? w : h;
        var result = new double[v.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int pos = horizontal ? x : y;
                for (int c = 0; c < 4; c++)
                {
                    double At(int i)
                    {
                        i = Math.Clamp(i, 0, length - 1);
                        return v[(((horizontal ? (y * w) + i : (i * w) + x)) * 4) + c];
                    }

                    double sum = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        sum += At(pos + k);
                    }

                    result[(((y * w) + x) * 4) + c] = (sum * ww) + ((At(pos - r - 1) + At(pos + r + 1)) * fw);
                }
            }
        }

        return result;
    }

    public static double[] GaussianBlur(double[] v, int w, int h, float sigma) =>
        BoxBlur(v, w, h, PictureSharp.Algorithms.Processing.Blur.GaussianBlurRadius(sigma, 3), 3);

    public static double[] UnsharpMask(double[] v, int w, int h, float radius, int percent, int threshold)
    {
        double[] blurred = GaussianBlur(v, w, h, radius);
        for (int i = 0; i < v.Length; i++)
        {
            double diff = v[i] - blurred[i];
            blurred[i] = Math.Abs(diff) > threshold / 255.0 ? Math.Clamp(v[i] + (diff * percent / 100), 0, 1) : v[i];
        }

        return blurred;
    }

    public static double[] Sharpen(double[] v, int w, int h)
    {
        var result = (double[])v.Clone();
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    double sum = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            sum += v[((((y + dy) * w) + x + dx) * 4) + c] * (dx == 0 && dy == 0 ? 2.0 : -1.0 / 8);
                        }
                    }

                    result[(((y * w) + x) * 4) + c] = Math.Clamp(sum, 0, 1);
                }
            }
        }

        return result;
    }

    // ------------------------------------------------------------ affine (Pillow's Geometry.c sampling)

    public static Rgba64[] Transform(Rgba64[] src, int srcWidth, int srcHeight, int width, int height, double[] m, bool bicubic, Rgba64 background)
    {
        double[] v = Premultiply(src);
        double[] fill = Premultiply([background]);
        var result = new double[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double xin = x + 0.5, yin = y + 0.5;
                double xx = (m[0] * xin) + (m[1] * yin) + m[2];
                double yy = (m[3] * xin) + (m[4] * yin) + m[5];
                bool inside = xx >= 0 && xx < srcWidth && yy >= 0 && yy < srcHeight;
                for (int c = 0; c < 4; c++)
                {
                    result[(((y * width) + x) * 4) + c] = !inside ? fill[c]
                        : bicubic ? Math.Clamp(SampleBicubic(v, srcWidth, srcHeight, xx - 0.5, yy - 0.5, c), 0, 1)
                        : SampleBilinear(v, srcWidth, srcHeight, xx - 0.5, yy - 0.5, c);
                }
            }
        }

        return Unpremultiply(result);
    }

    private static double SampleBilinear(double[] v, int w, int h, double x, double y, int c)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double dx = x - x0, dy = y - y0;
        double Row(int yy) => Lerp(At(v, w, h, x0, yy, c), At(v, w, h, x0 + 1, yy, c), dx);
        double top = Row(Math.Clamp(y0, 0, h - 1));
        double bottom = y0 + 1 < h ? Row(y0 + 1) : top;
        return Lerp(top, bottom, dy);
    }

    private static double SampleBicubic(double[] v, int w, int h, double x, double y, int c)
    {
        int x0 = (int)Math.Floor(x) - 1, y0 = (int)Math.Floor(y) - 1;
        double dx = x - (x0 + 1), dy = y - (y0 + 1);
        double Row(int yy) => Cubic(At(v, w, h, x0, yy, c), At(v, w, h, x0 + 1, yy, c), At(v, w, h, x0 + 2, yy, c), At(v, w, h, x0 + 3, yy, c), dx);
        // Rows above the image repeat the first row; rows below repeat the previous row (Pillow's BICUBIC_HEAD).
        var rows = new double[4];
        rows[0] = Row(Math.Clamp(y0, 0, h - 1));
        for (int i = 1; i < 4; i++)
        {
            rows[i] = y0 + i >= 0 && y0 + i < h ? Row(y0 + i) : rows[i - 1];
        }

        return Cubic(rows[0], rows[1], rows[2], rows[3], dy);
    }

    private static double At(double[] v, int w, int h, int x, int y, int c) => v[(((y * w) + Math.Clamp(x, 0, w - 1)) * 4) + c];

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    // Catmull-Rom style cubic of Pillow's BICUBIC macro (a = -0.5).
    private static double Cubic(double v1, double v2, double v3, double v4, double d) =>
        v2 + (d * ((-v1 + v3) + (d * (((2 * (v1 - v2)) + v3 - v4) + (d * (-v1 + v2 - v3 + v4))))));

    // ------------------------------------------------------------ composition (W3C separable blending)

    public static (double R, double G, double B, double A) Blend(BlendMode mode, (double R, double G, double B, double A) d,
        (double R, double G, double B, double A) s, double opacity)
    {
        double ab = d.A, alphaS = s.A * opacity, backdropWeight = ab * (1 - alphaS), ao = alphaS + backdropWeight;
        double C(double cb, double cs) => ((alphaS * (((1 - ab) * cs) + (ab * BlendFunction(mode, cb, cs)))) + (backdropWeight * cb)) / ao;
        return (C(d.R, s.R), C(d.G, s.G), C(d.B, s.B), ao);
    }

    private static double BlendFunction(BlendMode mode, double cb, double cs) => mode switch
    {
        BlendMode.Multiply => cb * cs,
        BlendMode.Screen => cb + cs - (cb * cs),
        BlendMode.SoftLight => cs <= 0.5 ? cb - ((1 - (2 * cs)) * cb * (1 - cb))
            : cb + (((2 * cs) - 1) * ((cb <= 0.25 ? ((((16 * cb) - 12) * cb) + 4) * cb : Math.Sqrt(cb)) - cb)),
        BlendMode.Difference => Math.Abs(cb - cs),
        _ => cs,
    };
}
