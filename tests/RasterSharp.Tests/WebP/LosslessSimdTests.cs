using RasterSharp.Formats.Png;
using RasterSharp.Formats.WebP;
using RasterSharp.Formats.WebP.Alpha;
using RasterSharp.Formats.WebP.Lossless;
using RasterSharp.Internal;

namespace RasterSharp.Tests.WebP;

/// <summary>SIMD paths of the lossless transforms and scanline filters must match the scalar code on every input.</summary>
public class LosslessSimdTests
{
    [Fact]
    public void AddGreen_MatchesScalar() =>
        AssertPixels(static (src, simd) =>
        {
            LosslessTransforms.AddGreenToBlueAndRedScalar(src);
            LosslessTransforms.AddGreenToBlueAndRed(simd);
        });

    [Fact]
    public void SubtractGreen_MatchesScalar() =>
        AssertPixels(static (src, simd) =>
        {
            LosslessTransforms.SubtractGreenFromBlueAndRedScalar(src);
            LosslessTransforms.SubtractGreenFromBlueAndRed(simd);
        });

    [Fact]
    public void TransformColor_MatchesScalar()
    {
        var rng = new Random(3);
        foreach (sbyte greenToRed in Sweep)
        foreach (sbyte greenToBlue in Anchors)
        foreach (sbyte redToBlue in Anchors)
        {
            uint[] scalar = RandomPixels(rng, 24);
            uint[] simd = (uint[])scalar.Clone();
            LosslessTransforms.TransformColorScalar(greenToRed, greenToBlue, redToBlue, scalar);
            LosslessTransforms.TransformColor(greenToRed, greenToBlue, redToBlue, simd);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void TransformColorInverse_MatchesScalar()
    {
        var rng = new Random(4);
        foreach (sbyte greenToRed in Sweep)
        foreach (sbyte greenToBlue in Anchors)
        foreach (sbyte redToBlue in Anchors)
        {
            uint[] scalar = RandomPixels(rng, 24);
            uint[] simd = (uint[])scalar.Clone();
            LosslessTransforms.TransformColorInverseScalar(greenToRed, greenToBlue, redToBlue, scalar);
            LosslessTransforms.TransformColorInverse(greenToRed, greenToBlue, redToBlue, simd);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void PredictorInverse_MatchesScalar()
    {
        var rng = new Random(5);
        int[] widths = [1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33];
        int[] heights = [1, 2, 3, 8];
        foreach (int width in widths)
        foreach (int height in heights)
        foreach (int bits in new[] { 0, 1, 2, 3 })
        {
            for (int mode = 0; mode < 16; mode++)
            {
                AssertPredictor(rng, width, height, bits, mode);
            }

            AssertPredictor(rng, width, height, bits, mode: -1);
        }

        AssertPredictor(rng, 320, 6, 3, mode: -1);
    }

    [Fact]
    public void ColorTransforms_ShortLengthsMatchScalar()
    {
        var rng = new Random(11);
        foreach (int length in new[] { 0, 1, 3, 4, 5, 6, 7, 9, 12, 15, 17, 31 })
        foreach (sbyte greenToRed in Anchors)
        foreach (sbyte greenToBlue in Anchors)
        foreach (sbyte redToBlue in Anchors)
        {
            uint[] pixels = RandomPixels(rng, length);
            uint[] forwardScalar = (uint[])pixels.Clone(), forwardSimd = (uint[])pixels.Clone();
            LosslessTransforms.TransformColorScalar(greenToRed, greenToBlue, redToBlue, forwardScalar);
            LosslessTransforms.TransformColor(greenToRed, greenToBlue, redToBlue, forwardSimd);
            Assert.Equal(forwardScalar, forwardSimd);
            uint[] inverseScalar = (uint[])pixels.Clone(), inverseSimd = (uint[])pixels.Clone();
            LosslessTransforms.TransformColorInverseScalar(greenToRed, greenToBlue, redToBlue, inverseScalar);
            LosslessTransforms.TransformColorInverse(greenToRed, greenToBlue, redToBlue, inverseSimd);
            Assert.Equal(inverseScalar, inverseSimd);
        }
    }

    [Fact]
    public void ArgbToRgba_MatchesScalar()
    {
        var rng = new Random(6);
        for (int length = 0; length <= 40; length++)
        {
            uint[] argb = RandomPixels(rng, length);
            Rgba32[] scalar = new Rgba32[length], simd = new Rgba32[length];
            WebPDecoder.ArgbToRgbaScalar(argb, scalar);
            WebPDecoder.ArgbToRgba(argb, simd);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void PngUnfilter_MatchesScalar()
    {
        var rng = new Random(7);
        foreach (int bpp in new[] { 1, 2, 3, 4, 6, 8 })
        foreach (int width in new[] { 1, 2, 3, 4, 5, 7, 8, 15, 16, 17, 31, 32 })
        foreach (int filter in new[] { 0, 1, 2, 3, 4 })
        {
            int rowBytes = width * bpp;
            int height = 3;
            byte[] scalar = new byte[(rowBytes + 1) * height];
            rng.NextBytes(scalar);
            for (int y = 0; y < height; y++)
            {
                scalar[y * (rowBytes + 1)] = (byte)filter;
            }

            byte[] simd = (byte[])scalar.Clone();
            PngDecoder.UnfilterScalar(scalar, 0, width, height, bpp * 8);
            PngDecoder.Unfilter(simd, 0, width, height, bpp * 8);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void AlphaUnfilter_MatchesScalar()
    {
        var rng = new Random(8);
        foreach (AlphaFilterType filter in new[] { AlphaFilterType.None, AlphaFilterType.Horizontal, AlphaFilterType.Vertical, AlphaFilterType.Gradient })
        foreach (int width in new[] { 1, 2, 3, 7, 8, 15, 16, 17, 31, 32, 33 })
        {
            byte[] scalar = new byte[width * 4];
            rng.NextBytes(scalar);
            byte[] simd = (byte[])scalar.Clone();
            AlphaDecoder.UnfilterScalar(filter, scalar, width, 4);
            AlphaDecoder.Unfilter(filter, simd, width, 4);
            Assert.Equal(scalar, simd);
        }
    }

    [Fact]
    public void PrefixSumBytes_MatchesScalar()
    {
        var rng = new Random(9);
        for (int length = 0; length <= 80; length++)
        {
            byte[] original = new byte[length];
            rng.NextBytes(original);
            byte seed = (byte)rng.Next(256);
            byte[] scalar = (byte[])original.Clone();
            ScanlineSimd.PrefixSumBytesScalar(scalar, seed);
            byte[] simd = (byte[])original.Clone();
            ScanlineSimd.PrefixSumBytes(simd, seed);
            Assert.Equal(scalar, simd);
        }
    }

    private static void AssertPredictor(Random rng, int width, int height, int bits, int mode)
    {
        int tilesPerRow = Vp8LDecoder.SubSampleSize(width, bits);
        int tiles = tilesPerRow * Vp8LDecoder.SubSampleSize(height, bits);
        uint[] modes = new uint[tiles];
        for (int i = 0; i < tiles; i++)
        {
            int value = mode < 0 ? rng.Next(16) : mode;
            modes[i] = WebPConstants.ArgbBlack | (uint)(value << 8);
        }

        uint[] scalar = RandomPixels(rng, width * height);
        uint[] simd = (uint[])scalar.Clone();
        var transform = new Vp8LTransform { Bits = bits, XSize = width, YSize = height, Data = modes };
        LosslessTransforms.PredictorInverseTransformScalar(transform, scalar);
        LosslessTransforms.PredictorInverseTransform(transform, simd);
        Assert.Equal(scalar, simd);
    }

    private static void AssertPixels(Action<uint[], uint[]> apply)
    {
        var rng = new Random(2);
        for (int length = 0; length <= 40; length++)
        {
            uint[] scalar = RandomPixels(rng, length);
            uint[] simd = (uint[])scalar.Clone();
            apply(scalar, simd);
            Assert.Equal(scalar, simd);
        }
    }

    private static uint[] RandomPixels(Random rng, int length)
    {
        uint[] pixels = new uint[length];
        for (int i = 0; i < length; i++)
        {
            pixels[i] = rng.Next(4) switch
            {
                0 => (uint)rng.Next(),
                1 => 0u,
                2 => 0xffffffffu,
                _ => (uint)((rng.Next(2) * 255) | ((rng.Next(2) * 255) << 8) | ((rng.Next(2) * 255) << 16) | ((rng.Next(2) * 255) << 24)),
            };
        }

        return pixels;
    }

    private static ReadOnlySpan<sbyte> Anchors => [-128, -64, -1, 0, 1, 64, 127];

    private static IEnumerable<sbyte> Sweep
    {
        get
        {
            for (int i = -128; i <= 127; i++)
            {
                yield return (sbyte)i;
            }
        }
    }
}
