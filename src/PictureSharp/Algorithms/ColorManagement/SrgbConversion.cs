namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>
/// Converts straight-alpha pixels from a matrix/TRC color model to sRGB with the relative colorimetric intent and
/// without black-point compensation: tone curves to linear light, colorants to XYZ (D50), the D50-adapted sRGB matrix
/// back to linear sRGB, clipping and the sRGB transfer function. Alpha is unchanged.
/// </summary>
internal sealed class SrgbConversion
{
    private readonly float[]? _red, _green, _blue;
    private readonly byte[]? _gray;
    private readonly float _m00, _m01, _m02, _m10, _m11, _m12, _m20, _m21, _m22;

    public SrgbConversion(IccColorModel model)
    {
        if (model.Colorants is not { } colorants)
        {
            // Gray: Y = TRC(gray) and the D50 white maps to the sRGB white, so the result is a neutral sRGB gray.
            // R, G and B are converted alike, which keeps gray pixels gray.
            _gray = new byte[256];
            for (int i = 0; i < _gray.Length; i++)
            {
                _gray[i] = SrgbTransfer.Encode((float)model.Red.Evaluate(i / 255.0));
            }

            return;
        }

        _red = model.Red.Sample8();
        _green = model.Green!.Sample8();
        _blue = model.Blue!.Sample8();
        ColorMatrix m = ColorMatrix.D50ToSrgb * colorants;
        (_m00, _m01, _m02) = ((float)m.M00, (float)m.M01, (float)m.M02);
        (_m10, _m11, _m12) = ((float)m.M10, (float)m.M11, (float)m.M12);
        (_m20, _m21, _m22) = ((float)m.M20, (float)m.M21, (float)m.M22);
    }

    /// <summary>Converts <paramref name="pixels"/> in place.</summary>
    public void Apply(Span<Rgba32> pixels) => Convert(pixels, pixels);

    /// <summary>Converts <paramref name="source"/> into <paramref name="target"/> of the same length, which may be the same span.</summary>
    public void Convert(ReadOnlySpan<Rgba32> source, Span<Rgba32> target)
    {
        if (_gray is { } gray)
        {
            for (int i = 0; i < source.Length; i++)
            {
                Rgba32 p = source[i];
                target[i] = new Rgba32(gray[p.R], gray[p.G], gray[p.B], p.A);
            }

            return;
        }

        float[] red = _red!, green = _green!, blue = _blue!;
        for (int i = 0; i < source.Length; i++)
        {
            Rgba32 p = source[i];
            float r = red[p.R], g = green[p.G], b = blue[p.B];
            target[i] = new Rgba32(
                SrgbTransfer.Encode((_m00 * r) + (_m01 * g) + (_m02 * b)),
                SrgbTransfer.Encode((_m10 * r) + (_m11 * g) + (_m12 * b)),
                SrgbTransfer.Encode((_m20 * r) + (_m21 * g) + (_m22 * b)),
                p.A);
        }
    }
}
