using PictureSharp.Algorithms.ColorManagement;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;
using PictureSharp.Primitives.Parallelism;

namespace PictureSharp.Algorithms.Hdr;

/// <summary>
/// Computes an ISO 21496-1 gain map from an SDR base image and its HDR rendition in linear light, as libultrahdr
/// v2.0.2 does with its default (best-quality, two-pass) encoder settings: box-averaged samples per map pixel, the gain
/// as log2 of the HDR to SDR ratio, bounds from the image's own gain range and an affine mapping to 8-bit codes. A
/// 16-bit base is averaged at its precision and linearized by interpolating its curves between 16-bit codes, so the map
/// describes the stored 16-bit samples; the map itself stays 8-bit.
/// </summary>
// port: libultrahdr v2.0.2 lib/src/jpegr.cpp UltraHdr::generateGainMap (generateGainMapTwoPass) and
// lib/src/gainmapmath.cpp samplePixels, srgbInvOetfLUT, computeGain, affineMapGain. libultrahdr keeps the gains of the
// first pass in a float buffer; here the second pass computes them again, which gives the same codes without a
// buffer of three floats per map pixel.
internal sealed class GainMapGenerator
{
    // port: lib/include/ultrahdr/gainmapmath.h
    private const float SdrWhiteNits = 203f;
    private const float Offset = 1e-7f;                        // kSdrOffset, kHdrOffset
    private const float MaxLinear = 10000f / 203f;             // kMaxPixelFloatHdrLinear
    private const int InvOetfEntries = 1 << 10;                // kSrgbInvOETFNumEntries
    private const float MinGain = -14.3f, MaxGain = 15.6f;
    private const float FltEpsilon = 1.1920929E-07f;

    // port: lib/src/gainmapmath.cpp kBt709ToP3 (row-major)
    private static readonly float[] SrgbToP3 = [0.822462f, 0.177537f, 0.000001f, 0.033194f, 0.966807f, -0.000001f, 0.017083f, 0.072398f, 0.91052f];

    private static readonly float[] InvOetfTable = CreateInvOetfTable();

    private readonly Rgba32[]? _sdr;
    private readonly Rgba64[]? _sdr16;
    private readonly float[]? _sdrLinear;
    private readonly float[]? _sdrLinear16;
    private readonly int _linearStride16;
    private readonly ReadOnlyMemory<Rgba128> _hdr;
    private readonly int _width;
    private readonly int _scale;
    private readonly int _channels;
    private readonly float[]? _sdrMatrix, _hdrMatrix;
    private readonly float _lumaR, _lumaG, _lumaB;

    /// <param name="sdr">The base pixels (8- or 16-bit, gamma encoded).</param>
    /// <param name="sdrSpace">Primaries of the base pixels.</param>
    /// <param name="sdrTransfer">The base's transfer function, or null for sRGB.</param>
    /// <param name="hdr">The HDR pixels in linear light, 1 = SDR white, the same size as the base.</param>
    /// <param name="hdrSpace">Primaries of the HDR pixels.</param>
    /// <param name="width">Width of both images.</param>
    /// <param name="height">Height of both images.</param>
    /// <param name="scale">The map is smaller than the images by this factor (1..128).</param>
    /// <param name="channels">1 for a luminance map, 3 for an RGB map.</param>
    public GainMapGenerator(PixelBuffer sdr, HdrColorSpace sdrSpace, BaseTransfer? sdrTransfer, ReadOnlyMemory<Rgba128> hdr, HdrColorSpace hdrSpace,
        int width, int height, int scale, int channels)
    {
        _sdr = sdr.Rgba32;
        _sdr16 = sdr.Rgba64;
        _sdrLinear = sdrTransfer?.Codes8;
        if (_sdr16 is not null)
        {
            (_sdrLinear16, _linearStride16) = sdrTransfer is { } transfer ? (transfer.Codes16, ushort.MaxValue + 1) : (SrgbTransfer.DecodeTable16, 0);
        }

        _hdr = hdr;
        _width = width;
        _channels = channels;

        // A scale that leaves no map pixel falls back to one map pixel per 8 image pixels of the shorter side.
        if (width / scale == 0 || height / scale == 0)
        {
            int shorter = Math.Min(width, height);
            scale = shorter >= 8 ? shorter / 8 : 1;
        }

        _scale = scale;
        MapWidth = width / scale;
        MapHeight = height / scale;

        // libultrahdr applies the gain in the wider of the two gamuts: here always Display P3 when they differ, which is
        // the base's for a P3 base and the HDR rendition's (with a map ICC profile) for an sRGB base.
        UseBaseColorSpace = sdrSpace == hdrSpace || sdrSpace == HdrColorSpace.LinearDisplayP3;
        MultiplicationSpace = UseBaseColorSpace ? sdrSpace : hdrSpace;
        if (sdrSpace != hdrSpace)
        {
            if (UseBaseColorSpace) _hdrMatrix = SrgbToP3; else _sdrMatrix = SrgbToP3;
        }

        // port: srgbLuminance, p3Luminance; libultrahdr takes the base's luminance for both renditions.
        (_lumaR, _lumaG, _lumaB) = sdrSpace == HdrColorSpace.LinearSrgb ? (0.212639f, 0.715169f, 0.072192f) : (0.2289746f, 0.6917385f, 0.0792869f);
    }

    public int MapWidth { get; }

    public int MapHeight { get; }

    /// <summary>Whether the gain is applied in the base's primaries (ISO <c>use_base_colour_space</c>).</summary>
    public bool UseBaseColorSpace { get; }

    /// <summary>The primaries in which the gain is applied.</summary>
    public HdrColorSpace MultiplicationSpace { get; }

    /// <summary>
    /// The map codes (gray maps repeat the value in R, G and B) and log2 gain bounds per channel (a gray map repeats
    /// them), for <paramref name="gamma"/>.
    /// </summary>
    public (DecodedGainMap Map, float[] Min, float[] Max) Generate(float gamma, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        // First pass: the gain range per row, merged in a fixed order.
        var rowMin = new float[MapHeight * 3];
        var rowMax = new float[MapHeight * 3];
        Rows(maxDegreeOfParallelism, cancellationToken, y =>
        {
            Span<float> gains = stackalloc float[3];
            float min0 = 127f, min1 = 127f, min2 = 127f, max0 = -128f, max1 = -128f, max2 = -128f;
            for (int x = 0; x < MapWidth; x++)
            {
                Gains(x, y, gains);
                min0 = Math.Min(gains[0], min0);
                max0 = Math.Max(gains[0], max0);
                if (_channels == 3)
                {
                    min1 = Math.Min(gains[1], min1);
                    max1 = Math.Max(gains[1], max1);
                    min2 = Math.Min(gains[2], min2);
                    max2 = Math.Max(gains[2], max2);
                }
            }

            (rowMin[y * 3], rowMin[(y * 3) + 1], rowMin[(y * 3) + 2]) = (min0, min1, min2);
            (rowMax[y * 3], rowMax[(y * 3) + 1], rowMax[(y * 3) + 2]) = (max0, max1, max2);
        });

        float[] min = [127f, 127f, 127f], max = [-128f, -128f, -128f];
        for (int y = 0; y < MapHeight; y++)
        {
            for (int c = 0; c < 3; c++)
            {
                min[c] = Math.Min(min[c], rowMin[(y * 3) + c]);
                max[c] = Math.Max(max[c], rowMax[(y * 3) + c]);
            }
        }

        // Gains in [-14.3, 15.6] represent every HDR pixel; wider bounds only cost precision in the affine mapping.
        for (int c = 0; c < _channels; c++)
        {
            min[c] = Math.Clamp(min[c], MinGain, MaxGain);
            max[c] = Math.Clamp(max[c], MinGain, MaxGain);
            if (MathF.Abs(max[c] - min[c]) < FltEpsilon)
            {
                max[c] += 0.1f;
            }
        }

        if (_channels == 1)
        {
            (min[1], min[2], max[1], max[2]) = (min[0], min[0], max[0], max[0]);
        }

        // Second pass: the same gains mapped to codes.
        Rgba32[] pixels = GC.AllocateUninitializedArray<Rgba32>(checked(MapWidth * MapHeight));
        Rows(maxDegreeOfParallelism, cancellationToken, y =>
        {
            Span<float> gains = stackalloc float[3];
            Span<Rgba32> row = pixels.AsSpan(y * MapWidth, MapWidth);
            for (int x = 0; x < MapWidth; x++)
            {
                Gains(x, y, gains);
                byte r = AffineMapGain(gains[0], min[0], max[0], gamma);
                row[x] = _channels == 3
                    ? new Rgba32(r, AffineMapGain(gains[1], min[1], max[1], gamma), AffineMapGain(gains[2], min[2], max[2], gamma), 255)
                    : new Rgba32(r, r, r, 255);
            }
        });

        return (new DecodedGainMap(pixels, MapWidth, MapHeight), min, max);
    }

    private void Rows(int maxDegreeOfParallelism, CancellationToken cancellationToken, Action<int> row)
    {
        if (MapHeight > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)_width * MapHeight * _scale, 1 << 16))
        {
            Parallel.For(0, MapHeight, Parallelism.Options(maxDegreeOfParallelism, cancellationToken), row);
        }
        else
        {
            for (int y = 0; y < MapHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                row(y);
            }
        }
    }

    // The log2 gains of map pixel (x, y): one per channel, or the luminance gain in gains[0].
    private void Gains(int x, int y, Span<float> gains)
    {
        // port: samplePixels — the box sum in row order, then one division.
        float sr = 0f, sg = 0f, sb = 0f, hr = 0f, hg = 0f, hb = 0f;
        ReadOnlySpan<Rgba128> hdr = _hdr.Span;
        Rgba32[]? sdr = _sdr;
        for (int dy = 0; dy < _scale; dy++)
        {
            int offset = (((y * _scale) + dy) * _width) + (x * _scale);
            for (int dx = 0; dx < _scale; dx++)
            {
                // port: getRgbaF16Pixel → sanitizePixel
                Rgba128 h = hdr[offset + dx];
                hr += Math.Clamp(h.R, 0f, MaxLinear);
                hg += Math.Clamp(h.G, 0f, MaxLinear);
                hb += Math.Clamp(h.B, 0f, MaxLinear);
            }

            if (sdr is not null)
            {
                foreach (Rgba32 s in sdr.AsSpan(offset, _scale))
                {
                    sr += s.R / 255f;
                    sg += s.G / 255f;
                    sb += s.B / 255f;
                }
            }
            else
            {
                foreach (Rgba64 s in _sdr16.AsSpan(offset, _scale))
                {
                    sr += s.R / 65535f;
                    sg += s.G / 65535f;
                    sb += s.B / 65535f;
                }
            }
        }

        float n = _scale * _scale;
        (sr, sg, sb) = _sdr is not null
            ? (Linearize(sr / n, 0), Linearize(sg / n, 256), Linearize(sb / n, 512))
            : (Linearize16(sr / n, 0), Linearize16(sg / n, 1), Linearize16(sb / n, 2));
        (hr, hg, hb) = (hr / n, hg / n, hb / n);
        if (_sdrMatrix is { } sm)
        {
            (sr, sg, sb) = Convert(sm, sr, sg, sb);
        }

        if (_hdrMatrix is { } hm)
        {
            (hr, hg, hb) = Convert(hm, hr, hg, hb);
        }

        (sr, sg, sb) = (Math.Max(sr, 0f), Math.Max(sg, 0f), Math.Max(sb, 0f));   // clipNegatives
        (hr, hg, hb) = (Math.Max(hr, 0f), Math.Max(hg, 0f), Math.Max(hb, 0f));
        if (_channels == 3)
        {
            gains[0] = ComputeGain(sr * SdrWhiteNits, hr * SdrWhiteNits);
            gains[1] = ComputeGain(sg * SdrWhiteNits, hg * SdrWhiteNits);
            gains[2] = ComputeGain(sb * SdrWhiteNits, hb * SdrWhiteNits);
        }
        else
        {
            float sdrY = ((_lumaR * sr) + (_lumaG * sg) + (_lumaB * sb)) * SdrWhiteNits;
            float hdrY = ((_lumaR * hr) + (_lumaG * hg) + (_lumaB * hb)) * SdrWhiteNits;
            gains[0] = ComputeGain(sdrY, hdrY);
        }
    }

    // sRGB through libultrahdr's 1024-entry table; another transfer function by interpolating its 256 code values.
    private float Linearize(float gamma, int channelOffset)
    {
        if (_sdrLinear is not { } table)
        {
            // port: srgbInvOetfLUT
            int index = (int)((gamma * (InvOetfEntries - 1)) + 0.5);
            return InvOetfTable[Math.Clamp(index, 0, InvOetfEntries - 1)];
        }

        float position = Math.Clamp(gamma, 0f, 1f) * 255f;
        int lower = Math.Min((int)position, 254);
        float t = position - lower;
        return table[channelOffset + lower] + ((table[channelOffset + lower + 1] - table[channelOffset + lower]) * t);
    }

    // A 16-bit base: its curve (or the sRGB one) interpolated between 16-bit codes, finer than the 10-bit sRGB table.
    private float Linearize16(float gamma, int channel)
    {
        float position = Math.Clamp(gamma, 0f, 1f) * 65535f;
        int lower = Math.Min((int)position, 65534);
        float t = position - lower;
        ReadOnlySpan<float> table = _sdrLinear16.AsSpan(channel * _linearStride16, ushort.MaxValue + 1);
        return table[lower] + ((table[lower + 1] - table[lower]) * t);
    }

    private static (float R, float G, float B) Convert(float[] m, float r, float g, float b) =>
        ((m[0] * r) + (m[1] * g) + (m[2] * b), (m[3] * r) + (m[4] * g) + (m[5] * b), (m[6] * r) + (m[7] * g) + (m[8] * b));

    // port: computeGain — dark base pixels may not signal large gains, which would blow out if they brighten slightly.
    // As in libultrahdr, log2 is taken in double precision and rounded to float, so the gain bounds (and the ISO
    // fractions written from them) are bit-identical to its; the rounded result does not depend on the platform.
    private static float ComputeGain(float sdr, float hdr)
    {
        float gain = (float)Math.Log2((hdr + Offset) / (sdr + Offset));
        return sdr < 2f / 255f ? Math.Min(gain, 2.3f) : gain;
    }

    // port: affineMapGain; a gain below the clamped minimum maps to 0 for every gamma.
    private static byte AffineMapGain(float gain, float min, float max, float gamma)
    {
        float mapped = (gain - min) / (max - min);
        if (gamma != 1f)
        {
            mapped = mapped > 0f ? (float)Math.Pow(mapped, gamma) : 0f;
        }

        return (byte)Math.Clamp((mapped * 255f) + 0.5f, 0f, 255f);
    }

    // port: LookUpTable(kSrgbInvOETFNumEntries, srgbInvOetf)
    private static float[] CreateInvOetfTable()
    {
        var table = new float[InvOetfEntries];
        for (int i = 0; i < table.Length; i++)
        {
            float e = i / (float)(InvOetfEntries - 1);
            table[i] = e <= 0.04045f ? e / 12.92f : (float)Math.Pow((e + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }
}
