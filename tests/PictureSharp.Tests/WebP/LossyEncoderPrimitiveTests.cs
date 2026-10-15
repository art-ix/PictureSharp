using System.Runtime.InteropServices;
using PictureSharp.Algorithms.ColorConversion;
using PictureSharp.Formats.WebP;
using PictureSharp.Infrastructure.Codecs.WebP.Alpha;
using PictureSharp.Infrastructure.Codecs.WebP.Lossy;
using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.WebP;

public class LossyEncoderPrimitiveTests
{
    [Fact]
    [Trait("Category", "Oracle")]
    public void ScalarPrimitives_MatchLibwebp160()
    {
        string root = Path.GetFullPath(Path.Combine(Corpus.Assets, "..", ".."));
        string reference = Path.Combine(root, "reference", "libwebp-1.6.0");
        string build = Path.Combine(root, "reference", "libwebp-1.6.0-build");
        if (!File.Exists(Path.Combine(build, "libwebp.a"))) return;
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-vp8-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string oracle = Path.Combine(directory, "oracle"), data = Path.Combine(directory, "primitives.bin");
            string source = Path.Combine(Corpus.Assets, "vp8_encode_primitives_oracle.c");
            Assert.True(ReferenceTools.Run("cc", $"-I\"{reference}\" \"{source}\" \"{build}/libwebp.a\" \"{build}/libsharpyuv.a\" -lm -lpthread -o \"{oracle}\"", out string error) == 0, error);
            Assert.True(ReferenceTools.Run(oracle, $"\"{data}\"", out string oracleOutput) == 0, oracleOutput);
            using var reader = new BinaryReader(File.OpenRead(data));
            int count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                byte[] pixels = reader.ReadBytes(512), prediction = reader.ReadBytes(512);
                byte[] left = reader.ReadBytes(33), top = reader.ReadBytes(20), boundary = reader.ReadBytes(13);
                short[] coefficients = new short[256], wht = new short[16];
                for (int b = 0; b < 16; b++) Vp8EncoderDsp.FTransform(pixels.AsSpan(Vp8EncoderDsp.Scan[b]), prediction.AsSpan(Vp8EncoderDsp.Scan[b]), coefficients.AsSpan(b * 16));
                Assert.Equal(ReadShorts(reader, 256), coefficients);
                Vp8EncoderDsp.FTransformWht(coefficients, wht);
                Assert.Equal(ReadShorts(reader, 16), wht);
                for (int type = 0; type < 3; type++)
                {
                    var matrix = new Vp8Matrix();
                    matrix.Expand(reader.ReadInt32(), reader.ReadInt32(), type);
                    short[] quantized = coefficients[..16], levels = new short[16];
                    Assert.Equal(reader.ReadInt32() != 0, Vp8EncoderDsp.QuantizeBlock(quantized, levels, matrix));
                    Assert.Equal(ReadShorts(reader, 16), quantized);
                    Assert.Equal(ReadShorts(reader, 16), levels);
                }
                byte[] predictions = new byte[1792];
                Vp8EncoderDsp.PredLuma16(predictions, (test & 1) != 0 ? left : default, (test & 2) != 0 ? top : default);
                Vp8EncoderDsp.PredChroma8(predictions, (test & 1) != 0 ? left : default, (test & 2) != 0 ? top : default);
                Vp8EncoderDsp.PredLuma4(predictions, boundary);
                Assert.Equal(reader.ReadBytes(1792), predictions);
                Assert.Equal(reader.ReadInt32(), Vp8EncoderDsp.Sse(pixels, prediction, 16, 16));
                Assert.Equal(reader.ReadInt32(), Vp8EncoderDsp.Disto(pixels, prediction, [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2], 16));
                Assert.Equal((reader.ReadInt32(), reader.ReadInt32()), Vp8EncoderDsp.CollectHistogram(pixels, prediction, 0, 16));
                using var writer = new Vp8BitWriter();
                for (int i = 0; i < 512; i++) writer.Put((pixels[i] & 1) != 0, prediction[i]);
                writer.PutBits(0xabcdef, 24); writer.PutSigned(-137, 8); writer.PutSigned(0, 8);
                Assert.Equal(reader.ReadBytes(reader.ReadInt32()), writer.Finish());
                for (int levels = 2; levels <= 256; levels *= 2)
                {
                    byte[] alpha = (byte[])pixels.Clone(); AlphaEncoder.Quantize(alpha, levels);
                    Assert.Equal(reader.ReadBytes(512), alpha);
                }
            }
            count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                int width = reader.ReadInt32(), height = reader.ReadInt32(), dithering = reader.ReadInt32();
                Rgba32[] pixels = MemoryMarshal.Cast<byte, Rgba32>(reader.ReadBytes(width * height * 4)).ToArray();
                using var yuv = new RgbToYuv(width, height);
                yuv.Convert(pixels, .5f * dithering);
                AssertPlanes(reader, yuv, $"convert {test}: {width}x{height}, dither={dithering}");
                yuv.CleanupTransparent(pixels);
                AssertPlanes(reader, yuv, $"cleanup {test}: {width}x{height}, dither={dithering}");
            }
            VerifyAnalysis(reader);
            VerifyEntropy(reader);
            count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                int width = reader.ReadInt32(), height = reader.ReadInt32();
                Rgba32[] pixels = MemoryMarshal.Cast<byte, Rgba32>(reader.ReadBytes(width * height * 4)).ToArray();
                using var yuv = new RgbToYuv(width, height);
                SharpYuv.Convert(pixels, yuv);
                AssertPlanes(reader, yuv, $"SharpYuv {test}: {width}x{height}");
            }
            Assert.Equal(reader.BaseStream.Length, reader.BaseStream.Position);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void VerifyEntropy(BinaryReader reader)
    {
        var probabilities = new Vp8EncoderProba(); var direct = new Vp8EncoderProba();
        reader.ReadBytes(1056).CopyTo(probabilities.Coefficients, 0);
        probabilities.Coefficients.CopyTo(direct.Coefficients, 0);
        probabilities.CalculateLevelCosts();
        using var tokens = new Vp8TokenBuffer();
        using var writer = new Vp8BitWriter();
        var tokenWriter = new Vp8CoefficientWriter(probabilities, tokens);
        var directWriter = new Vp8CoefficientWriter(direct, writer: writer);
        int count = reader.ReadInt32();
        for (int test = 0; test < count; test++)
        {
            int type = reader.ReadInt32(), context = reader.ReadInt32(), first = type == 0 ? 1 : 0;
            short[] levels = ReadShorts(reader, 16);
            Assert.Equal(reader.ReadInt32(), probabilities.ResidualCost(type, first, context, levels));
            tokenWriter.Write(type, first, context, levels); directWriter.Write(type, first, context, levels);
        }
        foreach (uint statistic in probabilities.Statistics) Assert.Equal(reader.ReadUInt32(), statistic);
        foreach (uint statistic in direct.Statistics) Assert.Equal(reader.ReadUInt32(), statistic);
        Assert.Equal(reader.ReadUInt32(), tokens.Emit(null, probabilities.Coefficients));
        byte[] expected = reader.ReadBytes(reader.ReadInt32());
        Assert.Equal(expected, writer.Finish());
        using var fromTokens = new Vp8BitWriter();
        tokens.Emit(fromTokens, probabilities.Coefficients);
        Assert.Equal(expected, fromTokens.Finish());
    }

    private static void VerifyAnalysis(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int test = 0; test < count; test++)
        {
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var options = new WebPEncoderOptions { Preset = (WebPPreset)reader.ReadInt32(), Method = reader.ReadInt32(), Quality = reader.ReadInt32(), Segments = reader.ReadInt32() };
            var config = new Vp8EncoderConfig(options);
            Assert.Equal(reader.ReadInt32(), config.SnsStrength);
            Assert.Equal(reader.ReadInt32(), config.FilterStrength);
            Assert.Equal(reader.ReadInt32(), config.FilterSharpness);
            using var source = new RgbToYuv(width, height);
            reader.ReadBytes(width * height).CopyTo(source.Y, 0);
            reader.ReadBytes(source.UvWidth * source.UvHeight).CopyTo(source.U, 0);
            reader.ReadBytes(source.UvWidth * source.UvHeight).CopyTo(source.V, 0);
            using var context = new Vp8EncoderContext(source, config);
            Vp8Analysis.Analyze(context);
            Vp8Quantization.SetSegmentParams(context, options.Quality);
            Assert.Equal(reader.ReadInt32(), context.Alpha); Assert.Equal(reader.ReadInt32(), context.UvAlpha);
            Assert.Equal(reader.ReadInt32(), context.SegmentCount); Assert.Equal(reader.ReadInt32(), context.BaseQuant);
            Assert.Equal(reader.ReadInt32(), context.DeltaUvDc); Assert.Equal(reader.ReadInt32(), context.DeltaUvAc);
            Assert.Equal(reader.ReadInt32(), context.FilterLevel);
            for (int i = 0; i < context.MbCount; i++)
            {
                Assert.Equal(reader.ReadByte(), context.Blocks[i].Type);
                Assert.Equal(reader.ReadByte(), context.Blocks[i].UvMode);
                Assert.Equal(reader.ReadByte(), context.Blocks[i].Segment);
                Assert.Equal(reader.ReadByte(), context.Blocks[i].Alpha);
            }
            int predSize = context.PredStride * (4 * context.MbHeight + 1);
            Assert.Equal(reader.ReadBytes(predSize), context.Modes[..predSize]);
            for (int i = 0; i < 4; i++)
            {
                Vp8Segment segment = context.Segments[i];
                Assert.Equal(reader.ReadInt32(), segment.Alpha); Assert.Equal(reader.ReadInt32(), segment.Beta);
                Assert.Equal(reader.ReadInt32(), segment.Quant); Assert.Equal(reader.ReadInt32(), segment.FilterStrength);
                if (i >= context.SegmentCount) continue;
                Assert.Equal(reader.ReadInt32(), segment.LambdaI4); Assert.Equal(reader.ReadInt32(), segment.LambdaI16);
                Assert.Equal(reader.ReadInt32(), segment.LambdaUv); Assert.Equal(reader.ReadInt32(), segment.LambdaMode);
                Assert.Equal(reader.ReadInt32(), segment.LambdaTrellisI4); Assert.Equal(reader.ReadInt32(), segment.LambdaTrellisI16);
                Assert.Equal(reader.ReadInt32(), segment.LambdaTrellisUv); Assert.Equal(reader.ReadInt32(), segment.TLambda);
                Assert.Equal(reader.ReadInt32(), segment.MinDisto); Assert.Equal(reader.ReadInt32(), segment.I4Penalty);
                AssertMatrix(reader, segment.Y1); AssertMatrix(reader, segment.Y2); AssertMatrix(reader, segment.Uv);
            }
            VerifyMacroblocks(reader, context, test);
        }
    }

    private static void VerifyMacroblocks(BinaryReader reader, Vp8EncoderContext context, int test)
    {
        var probabilities = new Vp8EncoderProba(); probabilities.CalculateLevelCosts();
        using var it = new Vp8EncoderIterator(context);
        do
        {
            it.Import();
            Vp8ModeScore rd = default;
            Vp8ModeDecision.Decimate(it, probabilities, ref rd, context.Config.RdLevel);
            string label = $"test={test}, method={context.Config.Options.Method}, q={context.Config.Options.Quality}, mb={it.X},{it.Y}";
            Assert.True(reader.ReadUInt32() == rd.Nz, $"nz: {label}");
            Assert.Equal(reader.ReadInt64(), rd.D); Assert.Equal(reader.ReadInt64(), rd.SD);
            Assert.Equal(reader.ReadInt64(), rd.R); Assert.Equal(reader.ReadInt64(), rd.H); Assert.Equal(reader.ReadInt64(), rd.Score);
            Assert.Equal(reader.ReadByte(), it.Block.Type); Assert.Equal(reader.ReadByte(), it.Block.UvMode);
            Assert.Equal(reader.ReadByte() != 0, it.Block.Skip);
            for (int y = 0; y < 4; y++) Assert.Equal(reader.ReadBytes(4), context.Modes[(it.PredOffset + y * context.PredStride)..(it.PredOffset + y * context.PredStride + 4)]);
            if (it.Block.Type == 1) Assert.Equal(ReadShorts(reader, 16), ((ReadOnlySpan<short>)rd.Dc).ToArray());
            Assert.Equal(ReadShorts(reader, 256), ((ReadOnlySpan<short>)rd.Ac).ToArray());
            Assert.Equal(ReadShorts(reader, 128), ((ReadOnlySpan<short>)rd.Uv).ToArray());
            for (int y = 0; y < 16; y++) Assert.Equal(reader.ReadBytes(16), it.Output[(y * 32)..(y * 32 + 16)]);
            for (int y = 0; y < 8; y++) Assert.Equal(reader.ReadBytes(16), it.Output[(y * 32 + 16)..(y * 32 + 32)]);
            uint dc = it.Block.Type == 1 ? rd.Nz & (1u << 24) : context.Nonzero[it.X + 1] & (1u << 24);
            context.Nonzero[it.X + 1] = rd.Nz | dc;
            if (it.Block.Type == 1) it.LeftNz[8] = (int)(rd.Nz >> 24) & 1;
            it.SaveBoundary();
        } while (it.Next());
    }

    private static void AssertMatrix(BinaryReader reader, Vp8Matrix matrix)
    {
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(reader.ReadUInt32(), matrix.Q[i]); Assert.Equal(reader.ReadUInt32(), matrix.Iq[i]);
            Assert.Equal(reader.ReadUInt32(), matrix.Bias[i]); Assert.Equal(reader.ReadUInt32(), matrix.ZeroThreshold[i]);
            Assert.Equal(reader.ReadUInt32(), matrix.Sharpen[i]);
        }
    }

    private static void AssertPlanes(BinaryReader reader, RgbToYuv yuv, string context)
    {
        int size = yuv.Width * yuv.Height, uvSize = yuv.UvWidth * yuv.UvHeight;
        Assert.True(reader.ReadBytes(size).AsSpan().SequenceEqual(yuv.Y.AsSpan(0, size)), $"Y: {context}");
        Assert.True(reader.ReadBytes(uvSize).AsSpan().SequenceEqual(yuv.U.AsSpan(0, uvSize)), $"U: {context}");
        Assert.True(reader.ReadBytes(uvSize).AsSpan().SequenceEqual(yuv.V.AsSpan(0, uvSize)), $"V: {context}");
    }

    private static short[] ReadShorts(BinaryReader reader, int count)
    {
        var result = new short[count];
        for (int i = 0; i < count; i++) result[i] = reader.ReadInt16();
        return result;
    }
}
