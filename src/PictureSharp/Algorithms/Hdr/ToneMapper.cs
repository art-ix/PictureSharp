using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// The deterministic HDR→SDR operator of <c>ToneMap</c>: conversion to linear sRGB, exposure, extended Reinhard on
/// Rec. 709 luminance with one scale for R, G and B, clipping to [0, 1], the sRGB transfer function and rounding to 8
/// bits through the sRGB table, or to 16 bits from <see cref="SrgbTransfer.EncodeUnit"/>.
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

    /// <summary>Maps one row to 16-bit pixels; returns whether any output pixel is not opaque.</summary>
    public bool Map(ReadOnlySpan<Rgba128> source, Span<Rgba64> target)
    {
        bool alpha = false;
        for (int i = 0; i < source.Length; i++)
        {
            Rgba128 p = source[i];
            (float r, float g, float b) = Operator(p);
            ushort a = (ushort)PixelDepth.Quantize(p.A, ushort.MaxValue);
            alpha |= a != ushort.MaxValue;
            target[i] = new Rgba64(Encode16(r), Encode16(g), Encode16(b), a);
        }

        return alpha;
    }

    /// <summary>Maps pixels from <paramref name="start"/> on; returns whether any of them is not opaque.</summary>
    internal bool MapScalar(ReadOnlySpan<Rgba128> source, Span<Rgba32> target, int start)
    {
        bool alpha = false;
        for (int i = start; i < source.Length; i++)
        {
            Rgba128 p = source[i];
            (float r, float g, float b) = Operator(p);
            byte a = Quantize(p.A);
            alpha |= a != 255;
            target[i] = new Rgba32(SrgbTransfer.Encode(r), SrgbTransfer.Encode(g), SrgbTransfer.Encode(b), a);   // clamps to [0, 1]
        }

        return alpha;
    }

    // Linear sRGB after the matrix, the exposure and the operator, not yet clipped.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (float R, float G, float B) Operator(Rgba128 p)
    {
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
            return (r * scale, g * scale, b * scale);
        }

        return (0f, 0f, 0f);
    }

    private static byte Quantize(float v) => (byte)((Math.Clamp(v, 0f, 1f) * 255f) + 0.5f);

    // Clamps (NaN to 0), encodes and rounds once to 16 bits.
    private static ushort Encode16(float linear) => (ushort)PixelDepth.Quantize(SrgbTransfer.EncodeUnit(linear), ushort.MaxValue);
}
