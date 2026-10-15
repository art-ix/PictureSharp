using PictureSharp.Algorithms.ColorConversion;
using PictureSharp.Infrastructure.Codecs.WebP.Lossy;
using PictureSharp.Primitives.Numerics;
using PictureSharp.Processing;

namespace PictureSharp.Tests.WebP;

/// <summary>The SIMD paths of the VP8 DSP must give exactly the scalar results, including for extreme inputs.</summary>
public class LossySimdTests
{
    private const int Bps = Vp8EncoderDsp.Bps;
    private const int Iterations = 4000;

    private static byte[] RandomBlock(Random rng, int length = 16 * Bps)
    {
        byte[] data = new byte[length];
        switch (rng.Next(4))
        {
            case 0: rng.NextBytes(data); break;
            case 1: data.AsSpan().Fill((byte)(rng.Next(2) * 255)); data[rng.Next(length)] ^= 0xFF; break;
            case 2: for (int i = 0; i < length; i++) data[i] = (byte)(rng.Next(2) * 255); break;
            default:
                int baseValue = rng.Next(256);
                for (int i = 0; i < length; i++) data[i] = (byte)Math.Clamp(baseValue + rng.Next(-6, 7), 0, 255);
                break;
        }

        return data;
    }

    private static short[] RandomCoefficients(Random rng)
    {
        short[] coefficients = new short[16];
        int range = rng.Next(4) switch { 0 => 32768, 1 => 2048, 2 => 256, _ => 16 };
        for (int i = 0; i < 16; i++) coefficients[i] = (short)Math.Clamp(rng.Next(-range, range), short.MinValue, short.MaxValue);
        if (rng.Next(4) == 0) coefficients.AsSpan(rng.Next(1, 16)).Clear();
        return coefficients;
    }

    [Fact]
    public void FTransform_MatchesScalar()
    {
        var rng = new Random(1);
        short[] expected = new short[16], actual = new short[16];
        for (int i = 0; i < Iterations; i++)
        {
            byte[] source = RandomBlock(rng), prediction = RandomBlock(rng);
            Vp8EncoderDsp.FTransformScalar(source, prediction, expected);
            Vp8EncoderDsp.FTransform(source, prediction, actual);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TransformOne_MatchesScalar()
    {
        var rng = new Random(2);
        for (int i = 0; i < Iterations; i++)
        {
            short[] coefficients = RandomCoefficients(rng);
            byte[] expected = RandomBlock(rng, Vp8Dsp.BlockSpan + 7), actual = (byte[])expected.Clone();
            int offset = rng.Next(8);
            Vp8Dsp.TransformOneScalar(coefficients, expected, offset);
            Vp8Dsp.TransformOne(coefficients, actual, offset);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ITransform_MatchesScalar()
    {
        var rng = new Random(3);
        for (int i = 0; i < Iterations; i++)
        {
            short[] coefficients = RandomCoefficients(rng);
            byte[] prediction = RandomBlock(rng, Vp8Dsp.BlockSpan);
            byte[] expected = RandomBlock(rng, 4 * Bps), actual = (byte[])expected.Clone();
            Vp8EncoderDsp.ITransformScalar(prediction, coefficients, expected, 4);
            Vp8EncoderDsp.ITransform(prediction, coefficients, actual, 4);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(16, 16)]
    [InlineData(16, 8)]
    [InlineData(4, 4)]
    public void Sse_MatchesScalar(int width, int height)
    {
        var rng = new Random(4 + width + height);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] a = RandomBlock(rng), b = RandomBlock(rng);
            Assert.Equal(Vp8EncoderDsp.SseScalar(a, b, width, height), Vp8EncoderDsp.Sse(a, b, width, height));
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    public void Disto_MatchesScalar(int size)
    {
        var rng = new Random(5 + size);
        ushort[] weights = new ushort[16];
        for (int i = 0; i < Iterations; i++)
        {
            byte[] a = RandomBlock(rng), b = RandomBlock(rng);
            for (int w = 0; w < 16; w++) weights[w] = (ushort)rng.Next(i % 2 == 0 ? 64 : 1024);
            Assert.Equal(Vp8EncoderDsp.DistoScalar(a, b, weights, size), Vp8EncoderDsp.Disto(a, b, weights, size));
        }
    }

    [Fact]
    public void QuantizeBlock_MatchesScalar()
    {
        var rng = new Random(6);
        for (int i = 0; i < Iterations; i++)
        {
            var matrix = new Vp8Matrix();
            matrix.Expand(rng.Next(4, 158), rng.Next(4, 285), rng.Next(3));
            short[] expectedInput = RandomCoefficients(rng), actualInput = (short[])expectedInput.Clone();
            short[] expected = new short[16], actual = new short[16];
            bool expectedNonZero = Vp8EncoderDsp.QuantizeBlockScalar(expectedInput, expected, matrix);
            bool actualNonZero = Vp8EncoderDsp.QuantizeBlock(actualInput, actual, matrix);
            Assert.Equal(expectedNonZero, actualNonZero);
            Assert.Equal(expected, actual);
            Assert.Equal(expectedInput, actualInput);
        }
    }
}

/// <summary>The SIMD YUV to RGB conversion must give exactly the scalar pixels.</summary>
public class YuvToRgbSimdTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Convert_MatchesScalar(bool fancy)
    {
        var rng = new Random(fancy ? 7 : 8);
        foreach (int width in new[] { 1, 2, 15, 16, 17, 18, 31, 33, 34, 35, 64, 67 })
            foreach (int height in new[] { 1, 2, 3, 4, 9 })
            {
                int uvWidth = (width + 1) / 2, uvHeight = (height + 1) / 2;
                var frame = new PictureSharp.Algorithms.ColorConversion.Yuv420Frame
                {
                    Width = width,
                    Height = height,
                    YStride = width + 3,
                    UvStride = uvWidth + 1,
                    Y = Fill(rng, (width + 3) * height),
                    U = Fill(rng, (uvWidth + 1) * uvHeight),
                    V = Fill(rng, (uvWidth + 1) * uvHeight),
                };
                var expected = new Rgba32[width * height];
                var actual = new Rgba32[width * height];
                PictureSharp.Algorithms.ColorConversion.YuvToRgb.Convert(frame, expected, fancy, allowSimd: false);
                PictureSharp.Algorithms.ColorConversion.YuvToRgb.Convert(frame, actual, fancy, allowSimd: true);
                Assert.Equal(expected, actual);
            }
    }

    private static byte[] Fill(Random rng, int length)
    {
        byte[] data = new byte[length];
        rng.NextBytes(data);
        // Extremes exercise the clipping of all three channels.
        for (int i = 0; i < length; i += 7) data[i] = (byte)(rng.Next(2) * 255);
        return data;
    }
}

/// <summary>The SIMD in-loop filters must give exactly the scalar results.</summary>
public class LoopFilterSimdTests
{
    private const int Stride = 40, Origin = 4 * Stride + 8;

    private delegate void Luma(byte[] p, int o, int stride, int thresh, int ithresh, int hevThresh);
    private delegate void Chroma(byte[] u, int uo, byte[] v, int vo, int stride, int thresh, int ithresh, int hevThresh);
    private delegate void Simple(byte[] p, int o, int stride, int thresh);

    private static byte[] Plane(Random rng)
    {
        byte[] data = new byte[Stride * 28];
        int baseValue = rng.Next(256), spread = rng.Next(4) switch { 0 => 2, 1 => 8, 2 => 40, _ => 256 };
        for (int i = 0; i < data.Length; i++) data[i] = (byte)Math.Clamp(baseValue + rng.Next(-spread, spread + 1), 0, 255);
        if (rng.Next(3) == 0)
        {
            // A step across the edges, as at block boundaries.
            int step = rng.Next(-60, 61);
            for (int i = 0; i < data.Length; i++) if (i % Stride >= 16 || i / Stride >= 12) data[i] = (byte)Math.Clamp(data[i] + step, 0, 255);
        }

        return data;
    }

    private static (int Thresh, int IThresh, int Hev) Thresholds(Random rng) => (rng.Next(0, 128), rng.Next(0, 64), rng.Next(0, 4));

    public static TheoryData<string> LumaFilters() => new() { "V16", "H16", "V16i", "H16i" };

    private static (Luma Scalar, Luma Simd) LumaPair(string name) => name switch
    {
        "V16" => (Vp8Dsp.VFilter16Scalar, Vp8Dsp.VFilter16),
        "H16" => (Vp8Dsp.HFilter16Scalar, Vp8Dsp.HFilter16),
        "V16i" => (Vp8Dsp.VFilter16iScalar, Vp8Dsp.VFilter16i),
        _ => (Vp8Dsp.HFilter16iScalar, Vp8Dsp.HFilter16i),
    };

    [Theory]
    [MemberData(nameof(LumaFilters))]
    public void LumaFilter_MatchesScalar(string name)
    {
        var (scalar, simd) = LumaPair(name);
        var rng = new Random(name.GetHashCode(StringComparison.Ordinal) & 0xffff);
        for (int i = 0; i < 3000; i++)
        {
            byte[] expected = Plane(rng), actual = (byte[])expected.Clone();
            var (thresh, ithresh, hev) = Thresholds(rng);
            scalar(expected, Origin, Stride, thresh, ithresh, hev);
            simd(actual, Origin, Stride, thresh, ithresh, hev);
            Assert.Equal(expected, actual);
        }
    }

    public static TheoryData<string> ChromaFilters() => new() { "V8", "H8", "V8i", "H8i" };

    private static (Chroma Scalar, Chroma Simd) ChromaPair(string name) => name switch
    {
        "V8" => (Vp8Dsp.VFilter8Scalar, Vp8Dsp.VFilter8),
        "H8" => (Vp8Dsp.HFilter8Scalar, Vp8Dsp.HFilter8),
        "V8i" => (Vp8Dsp.VFilter8iScalar, Vp8Dsp.VFilter8i),
        _ => (Vp8Dsp.HFilter8iScalar, Vp8Dsp.HFilter8i),
    };

    [Theory]
    [MemberData(nameof(ChromaFilters))]
    public void ChromaFilter_MatchesScalar(string name)
    {
        var (scalar, simd) = ChromaPair(name);
        var rng = new Random(name.GetHashCode(StringComparison.Ordinal) & 0xffff);
        for (int i = 0; i < 3000; i++)
        {
            byte[] expectedU = Plane(rng), expectedV = Plane(rng);
            byte[] actualU = (byte[])expectedU.Clone(), actualV = (byte[])expectedV.Clone();
            var (thresh, ithresh, hev) = Thresholds(rng);
            scalar(expectedU, Origin, expectedV, Origin + 1, Stride, thresh, ithresh, hev);
            simd(actualU, Origin, actualV, Origin + 1, Stride, thresh, ithresh, hev);
            Assert.Equal(expectedU, actualU);
            Assert.Equal(expectedV, actualV);
        }
    }

    public static TheoryData<string> SimpleFilters() => new() { "V", "H", "Vi", "Hi" };

    [Theory]
    [MemberData(nameof(SimpleFilters))]
    public void SimpleFilter_MatchesScalar(string name)
    {
        (Simple scalar, Simple simd) = name switch
        {
            "V" => ((Simple)Vp8Dsp.SimpleVFilter16Scalar, (Simple)Vp8Dsp.SimpleVFilter16),
            "H" => (Vp8Dsp.SimpleHFilter16Scalar, Vp8Dsp.SimpleHFilter16),
            "Vi" => (Vp8Dsp.SimpleVFilter16iScalar, Vp8Dsp.SimpleVFilter16i),
            _ => (Vp8Dsp.SimpleHFilter16iScalar, Vp8Dsp.SimpleHFilter16i),
        };
        var rng = new Random(name.Length * 31 + name[0]);
        for (int i = 0; i < 3000; i++)
        {
            byte[] expected = Plane(rng), actual = (byte[])expected.Clone();
            int thresh = rng.Next(0, 128);
            scalar(expected, Origin, Stride, thresh);
            simd(actual, Origin, Stride, thresh);
            Assert.Equal(expected, actual);
        }
    }
}
