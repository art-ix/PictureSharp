using PictureSharp.Algorithms.ColorManagement;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// The deterministic HDR→SDR operator of <c>ToneMap</c>: conversion to linear sRGB, exposure, extended Reinhard on
/// Rec. 709 luminance with one scale for R, G and B, clipping to [0, 1], the sRGB transfer function and rounding.
/// </summary>
internal sealed partial class ToneMapper
{
    private readonly HdrColorMatrix? _toSrgb;
    private readonly float _exposure;
    private readonly float _inverseWhiteSquared;

    public ToneMapper(HdrColorSpace source, float exposure, float whitePoint)
    {
        _toSrgb = HdrColorMatrix.Between(source, HdrColorSpace.LinearSrgb);

        _exposure = float.Exp2(exposure);
        _inverseWhiteSquared = 1f / (whitePoint * whitePoint);
    }

    /// <summary>Maps one row; returns whether any output pixel is not opaque.</summary>
    public bool Map(ReadOnlySpan<Rgba128> source, Span<Rgba32> target)
    {
        int done = MapSimd(source, target, out bool alpha);
        return MapScalar(source, target, done) | alpha;
    }

    /// <summary>Maps pixels from <paramref name="start"/> on; returns whether any of them is not opaque.</summary>
    internal bool MapScalar(ReadOnlySpan<Rgba128> source, Span<Rgba32> target, int start)
    {
        bool alpha = false;
        for (int i = start; i < source.Length; i++)
        {
            Rgba128 p = source[i];
            float r = p.R, g = p.G, b = p.B;
            if (_toSrgb is { } matrix)
            {
                (r, g, b) = matrix.Transform(r, g, b);
            }

            r *= _exposure;
            g *= _exposure;
            b *= _exposure;
            float l = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
            if (l > 0f)
            {
                float scale = (1f + (l * _inverseWhiteSquared)) / (1f + l);   // Lout / L
                r *= scale;
                g *= scale;
                b *= scale;
            }
            else
            {
                r = g = b = 0f;
            }

            byte a = Quantize(p.A);
            alpha |= a != 255;
            target[i] = new Rgba32(SrgbTransfer.Encode(r), SrgbTransfer.Encode(g), SrgbTransfer.Encode(b), a);   // clamps to [0, 1]
        }

        return alpha;
    }

    private static byte Quantize(float v) => (byte)((Math.Clamp(v, 0f, 1f) * 255f) + 0.5f);
}
