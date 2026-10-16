using System.Drawing;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Rebuilds linear HDR pixels from base pixels and a decoded gain map: linearize the base, move to the multiplication
/// primaries, apply the gain, move to the output primaries. Each output pixel samples the map at the position of its
/// source pixel in the stored base image, through the applied orientation and thumbnail scale, so no enlarged map is
/// allocated.
/// </summary>
internal sealed class GainMapReconstructor
{
    private readonly GainMapParameters _p;
    private readonly DecodedGainMap _map;
    private readonly float[] _linear;      // 768 entries: R, G, B codes → linear
    private readonly float[]? _toMultiply; // 3×3, null for identity
    private readonly float[]? _toOutput;   // 3×3, null for identity
    private readonly float _weight;        // ISO weight of the alternate rendition
    private readonly float _appleFactor;   // Apple: headroom − 1
    private readonly int _width, _height;
    private readonly ExifOrientation _orientation;
    private readonly int _storedWidth, _storedHeight;
    private readonly int _orientedWidth, _orientedHeight;  // the image before orientation
    private readonly float _mapScaleX, _mapScaleY;

    /// <param name="parameters">Gain map model and values.</param>
    /// <param name="map">The decoded map.</param>
    /// <param name="baseSpace">Primaries of the base pixels.</param>
    /// <param name="baseLinearization">768 linear values of the base codes, or null for sRGB.</param>
    /// <param name="outputSpace">Primaries of the result.</param>
    /// <param name="targetHeadroom">Linear target headroom, or null for the source's maximum.</param>
    /// <param name="size">Size of the base pixels.</param>
    /// <param name="storedSize">Stored size of the base image (before thumbnail and orientation).</param>
    /// <param name="orientation">Orientation applied to the base pixels while loading.</param>
    public GainMapReconstructor(GainMapParameters parameters, DecodedGainMap map, HdrColorSpace baseSpace, float[]? baseLinearization,
        HdrColorSpace outputSpace, float? targetHeadroom, Size size, Size storedSize, ExifOrientation orientation)
    {
        _p = parameters;
        _map = map;
        _linear = baseLinearization ?? SrgbTables();
        HdrColorSpace multiplySpace = parameters.MultiplicationSpace ?? baseSpace;
        _toMultiply = Matrix(baseSpace, multiplySpace);
        _toOutput = Matrix(multiplySpace, outputSpace);
        (_width, _height) = (size.Width, size.Height);
        (_storedWidth, _storedHeight) = (storedSize.Width, storedSize.Height);
        _orientation = orientation;
        bool swaps = orientation is ExifOrientation.LeftTop or ExifOrientation.RightTop or ExifOrientation.RightBottom or ExifOrientation.LeftBottom;
        (_orientedWidth, _orientedHeight) = swaps ? (_height, _width) : (_width, _height);

        // As libultrahdr: map coordinate = base coordinate / (base width / map width), one factor for both axes when
        // the aspect ratios differ by at most 1%. libultrahdr resizes other maps to the base size first; here each
        // axis gets its own factor instead, which needs no full-size map.
        _mapScaleX = (float)_storedWidth / map.Width;
        float baseAspect = (float)_storedWidth / _storedHeight, mapAspect = (float)map.Width / map.Height;
        _mapScaleY = MathF.Abs(baseAspect - mapAspect) / baseAspect <= 0.01f ? _mapScaleX : (float)_storedHeight / map.Height;

        float targetLog2 = targetHeadroom is { } t ? MathF.Log2(t) : float.PositiveInfinity;
        if (parameters.Kind == HdrGainMapKinds.Iso21496)
        {
            _weight = targetHeadroom is null ? 1f : GainMapMath.IsoWeight(targetLog2, parameters.BaseHeadroom, parameters.AlternateHeadroom);
            Headroom = float.Exp2(parameters.BaseHeadroom + (_weight * (parameters.AlternateHeadroom - parameters.BaseHeadroom)));
        }
        else
        {
            Headroom = float.Exp2(Math.Min(targetLog2, parameters.AlternateHeadroom));
            _appleFactor = Headroom - 1f;
        }
    }

    /// <summary>The effective linear headroom of the reconstruction.</summary>
    public float Headroom { get; }

    /// <summary>The reduction (1, 2, 4 or 8) at which the map still has at least the resolution the output samples.</summary>
    public static int MapScale(Size mapSize, Size size, Size storedSize, ExifOrientation orientation)
    {
        bool swaps = orientation is ExifOrientation.LeftTop or ExifOrientation.RightTop or ExifOrientation.RightBottom or ExifOrientation.LeftBottom;
        (int w, int h) = swaps ? (size.Height, size.Width) : (size.Width, size.Height);
        foreach (int scale in (ReadOnlySpan<int>)[8, 4, 2])
        {
            if ((long)w * scale <= storedSize.Width && (long)h * scale <= storedSize.Height && mapSize.Width >= scale && mapSize.Height >= scale)
            {
                return scale;
            }
        }

        return 1;
    }

    /// <summary>Reconstructs all rows of <paramref name="basePixels"/> into <paramref name="output"/>.</summary>
    public void Run(Rgba32[] basePixels, Rgba128[] output, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        if (_height > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)_width * _height, 1 << 16))
        {
            Parallel.For(0, _height, Parallelism.Options(maxDegreeOfParallelism, cancellationToken), y => Row(basePixels, output, y));
        }
        else
        {
            for (int y = 0; y < _height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row(basePixels, output, y);
            }
        }
    }

    private void Row(Rgba32[] basePixels, Rgba128[] output, int y)
    {
        ReadOnlySpan<Rgba32> source = basePixels.AsSpan(y * _width, _width);
        Span<Rgba128> target = output.AsSpan(y * _width, _width);
        ReadOnlySpan<float> linear = _linear;
        ReadOnlySpan<Rgba32> map = _map.Pixels;
        bool iso = _p.Kind == HdrGainMapKinds.Iso21496;
        bool mono = _p.ChannelCount == 1;
        for (int x = 0; x < source.Length; x++)
        {
            Rgba32 p = source[x];
            float r = linear[p.R], g = linear[256 + p.G], b = linear[512 + p.B];
            if (_toMultiply is { } m)
            {
                (r, g, b) = Transform(m, r, g, b);
            }

            (float bx, float by) = BasePosition(x, y);
            if (iso)
            {
                float mx = Math.Max(bx, 0f) / _mapScaleX, my = Math.Max(by, 0f) / _mapScaleY;
                float gr = GainMapMath.SampleIdw(map, _map.Width, _map.Height, mx, my, 0);
                float fr = GainMapMath.IsoGainFactor(gr, _p.GainMapMin[0], _p.GainMapMax[0], _p.Gamma[0], _weight);
                float fg = fr, fb = fr;
                if (!mono)
                {
                    fg = GainMapMath.IsoGainFactor(GainMapMath.SampleIdw(map, _map.Width, _map.Height, mx, my, 1),
                        _p.GainMapMin[1], _p.GainMapMax[1], _p.Gamma[1], _weight);
                    fb = GainMapMath.IsoGainFactor(GainMapMath.SampleIdw(map, _map.Width, _map.Height, mx, my, 2),
                        _p.GainMapMin[2], _p.GainMapMax[2], _p.Gamma[2], _weight);
                }

                r = ((r + _p.BaseOffset[0]) * fr) - _p.AlternateOffset[0];
                g = ((g + _p.BaseOffset[1]) * fg) - _p.AlternateOffset[1];
                b = ((b + _p.BaseOffset[2]) * fb) - _p.AlternateOffset[2];
            }
            else
            {
                // Pixel centers of the base mapped onto pixel centers of the map.
                float mx = ((bx + 0.5f) / _mapScaleX) - 0.5f, my = ((by + 0.5f) / _mapScaleY) - 0.5f;
                float gain = GainMapMath.Rec709ToLinear(GainMapMath.SampleBilinear(map, _map.Width, _map.Height, mx, my, 0));
                float factor = 1f + (_appleFactor * gain);
                (r, g, b) = (r * factor, g * factor, b * factor);
            }

            if (_toOutput is { } o)
            {
                (r, g, b) = Transform(o, r, g, b);
            }

            target[x] = new Rgba128(r, g, b, p.A / 255f);
        }
    }

    /// <summary>The position in the stored base image of the output pixel (x, y): undo the orientation, then the scale.</summary>
    private (float X, float Y) BasePosition(int x, int y)
    {
        int ow = _orientedWidth, oh = _orientedHeight;
        (int sx, int sy) = _orientation switch
        {
            ExifOrientation.TopRight => (ow - 1 - x, y),
            ExifOrientation.BottomRight => (ow - 1 - x, oh - 1 - y),
            ExifOrientation.BottomLeft => (x, oh - 1 - y),
            ExifOrientation.LeftTop => (y, x),
            ExifOrientation.RightTop => (y, oh - 1 - x),
            ExifOrientation.RightBottom => (ow - 1 - y, oh - 1 - x),
            ExifOrientation.LeftBottom => (ow - 1 - y, x),
            _ => (x, y),
        };

        if (ow == _storedWidth && oh == _storedHeight)
        {
            return (sx, sy);
        }

        return (((sx + 0.5f) * _storedWidth / ow) - 0.5f, ((sy + 0.5f) * _storedHeight / oh) - 0.5f);
    }

    private static (float, float, float) Transform(float[] m, float r, float g, float b) =>
        ((m[0] * r) + (m[1] * g) + (m[2] * b), (m[3] * r) + (m[4] * g) + (m[5] * b), (m[6] * r) + (m[7] * g) + (m[8] * b));

    private static float[]? Matrix(HdrColorSpace source, HdrColorSpace target)
    {
        if (source == target)
        {
            return null;
        }

        ColorMatrix c = HdrColorSpaces.Conversion(source, target);
        return [(float)c.M00, (float)c.M01, (float)c.M02, (float)c.M10, (float)c.M11, (float)c.M12, (float)c.M20, (float)c.M21, (float)c.M22];
    }

    private static float[] SrgbTables()
    {
        var table = new float[768];
        for (int i = 0; i < 3; i++)
        {
            SrgbTransfer.DecodeTable.CopyTo(table.AsSpan(i * 256, 256));
        }

        return table;
    }
}
