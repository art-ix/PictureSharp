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
    [Test]
    [Property("Category", "Oracle")]
    public async Task ScalarPrimitives_MatchLibwebp160()
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
            await Assert.That(ReferenceTools.Run("cc", $"-I\"{reference}\" \"{source}\" \"{build}/libwebp.a\" \"{build}/libsharpyuv.a\" -lm -lpthread -o \"{oracle}\"", out string error) == 0).IsTrue().Because(error);
            await Assert.That(ReferenceTools.Run(oracle, $"\"{data}\"", out string oracleOutput) == 0).IsTrue().Because(oracleOutput);
            using var reader = new BinaryReader(File.OpenRead(data));
            int count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                byte[] pixels = reader.ReadBytes(512), prediction = reader.ReadBytes(512);
                byte[] left = reader.ReadBytes(33), top = reader.ReadBytes(20), boundary = reader.ReadBytes(13);
                short[] coefficients = new short[256], wht = new short[16];
                for (int b = 0; b < 16; b++) Vp8EncoderDsp.FTransform(pixels.AsSpan(Vp8EncoderDsp.Scan[b]), prediction.AsSpan(Vp8EncoderDsp.Scan[b]), coefficients.AsSpan(b * 16));
                await Assert.That(coefficients).IsEquivalentTo(ReadShorts(reader, 256), CollectionOrdering.Matching);
                Vp8EncoderDsp.FTransformWht(coefficients, wht);
                await Assert.That(wht).IsEquivalentTo(ReadShorts(reader, 16), CollectionOrdering.Matching);
                for (int type = 0; type < 3; type++)
                {
                    var matrix = new Vp8Matrix();
                    matrix.Expand(reader.ReadInt32(), reader.ReadInt32(), type);
                    short[] quantized = coefficients[..16], levels = new short[16];
                    await Assert.That(Vp8EncoderDsp.QuantizeBlock(quantized, levels, matrix)).IsEqualTo(reader.ReadInt32() != 0);
                    await Assert.That(quantized).IsEquivalentTo(ReadShorts(reader, 16), CollectionOrdering.Matching);
                    await Assert.That(levels).IsEquivalentTo(ReadShorts(reader, 16), CollectionOrdering.Matching);
                }
                byte[] predictions = new byte[1792];
                Vp8EncoderDsp.PredLuma16(predictions, (test & 1) != 0 ? left : default, (test & 2) != 0 ? top : default);
                Vp8EncoderDsp.PredChroma8(predictions, (test & 1) != 0 ? left : default, (test & 2) != 0 ? top : default);
                Vp8EncoderDsp.PredLuma4(predictions, boundary);
                await Assert.That(predictions).IsEquivalentTo(reader.ReadBytes(1792), CollectionOrdering.Matching);
                await Assert.That(Vp8EncoderDsp.Sse(pixels, prediction, 16, 16)).IsEqualTo(reader.ReadInt32());
                await Assert.That(Vp8EncoderDsp.Disto(pixels, prediction, [38, 32, 20, 9, 32, 28, 17, 7, 20, 17, 10, 4, 9, 7, 4, 2], 16)).IsEqualTo(reader.ReadInt32());
                await Assert.That(Vp8EncoderDsp.CollectHistogram(pixels, prediction, 0, 16)).IsEqualTo((reader.ReadInt32(), reader.ReadInt32()));
                using var writer = new Vp8BitWriter();
                for (int i = 0; i < 512; i++) writer.Put((pixels[i] & 1) != 0, prediction[i]);
                writer.PutBits(0xabcdef, 24); writer.PutSigned(-137, 8); writer.PutSigned(0, 8);
                await Assert.That(writer.Finish()).IsEquivalentTo(reader.ReadBytes(reader.ReadInt32()), CollectionOrdering.Matching);
                for (int levels = 2; levels <= 256; levels *= 2)
                {
                    byte[] alpha = (byte[])pixels.Clone(); AlphaEncoder.Quantize(alpha, levels);
                    await Assert.That(alpha).IsEquivalentTo(reader.ReadBytes(512), CollectionOrdering.Matching);
                }
            }
            count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                int width = reader.ReadInt32(), height = reader.ReadInt32(), dithering = reader.ReadInt32();
                Rgba32[] pixels = MemoryMarshal.Cast<byte, Rgba32>(reader.ReadBytes(width * height * 4)).ToArray();
                using var yuv = new RgbToYuv(width, height);
                yuv.Convert(pixels, .5f * dithering);
                await AssertPlanes(reader, yuv, $"convert {test}: {width}x{height}, dither={dithering}");
                yuv.CleanupTransparent(pixels);
                await AssertPlanes(reader, yuv, $"cleanup {test}: {width}x{height}, dither={dithering}");
            }
            await VerifyAnalysis(reader);
            await VerifyEntropy(reader);
            count = reader.ReadInt32();
            for (int test = 0; test < count; test++)
            {
                int width = reader.ReadInt32(), height = reader.ReadInt32();
                Rgba32[] pixels = MemoryMarshal.Cast<byte, Rgba32>(reader.ReadBytes(width * height * 4)).ToArray();
                using var yuv = new RgbToYuv(width, height);
                SharpYuv.Convert(pixels, yuv);
                await AssertPlanes(reader, yuv, $"SharpYuv {test}: {width}x{height}");
            }
            await Assert.That(reader.BaseStream.Position).IsEqualTo(reader.BaseStream.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task VerifyEntropy(BinaryReader reader)
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
            await Assert.That(probabilities.ResidualCost(type, first, context, levels)).IsEqualTo(reader.ReadInt32());
            tokenWriter.Write(type, first, context, levels); directWriter.Write(type, first, context, levels);
        }
        foreach (uint statistic in probabilities.Statistics) await Assert.That(statistic).IsEqualTo(reader.ReadUInt32());
        foreach (uint statistic in direct.Statistics) await Assert.That(statistic).IsEqualTo(reader.ReadUInt32());
        await Assert.That(tokens.Emit(null, probabilities.Coefficients)).IsEqualTo(reader.ReadUInt32());
        byte[] expected = reader.ReadBytes(reader.ReadInt32());
        await Assert.That(writer.Finish()).IsEquivalentTo(expected, CollectionOrdering.Matching);
        using var fromTokens = new Vp8BitWriter();
        tokens.Emit(fromTokens, probabilities.Coefficients);
        await Assert.That(fromTokens.Finish()).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    private static async Task VerifyAnalysis(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int test = 0; test < count; test++)
        {
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            var options = new WebPEncoderOptions { Preset = (WebPPreset)reader.ReadInt32(), Method = reader.ReadInt32(), Quality = reader.ReadInt32(), Segments = reader.ReadInt32() };
            var config = new Vp8EncoderConfig(options);
            await Assert.That(config.SnsStrength).IsEqualTo(reader.ReadInt32());
            await Assert.That(config.FilterStrength).IsEqualTo(reader.ReadInt32());
            await Assert.That(config.FilterSharpness).IsEqualTo(reader.ReadInt32());
            using var source = new RgbToYuv(width, height);
            reader.ReadBytes(width * height).CopyTo(source.Y, 0);
            reader.ReadBytes(source.UvWidth * source.UvHeight).CopyTo(source.U, 0);
            reader.ReadBytes(source.UvWidth * source.UvHeight).CopyTo(source.V, 0);
            using var context = new Vp8EncoderContext(source, config);
            Vp8Analysis.Analyze(context);
            Vp8Quantization.SetSegmentParams(context, options.Quality);
            await Assert.That(context.Alpha).IsEqualTo(reader.ReadInt32()); await Assert.That(context.UvAlpha).IsEqualTo(reader.ReadInt32());
            await Assert.That(context.SegmentCount).IsEqualTo(reader.ReadInt32()); await Assert.That(context.BaseQuant).IsEqualTo(reader.ReadInt32());
            await Assert.That(context.DeltaUvDc).IsEqualTo(reader.ReadInt32()); await Assert.That(context.DeltaUvAc).IsEqualTo(reader.ReadInt32());
            await Assert.That(context.FilterLevel).IsEqualTo(reader.ReadInt32());
            for (int i = 0; i < context.MbCount; i++)
            {
                await Assert.That(context.Blocks[i].Type).IsEqualTo(reader.ReadByte());
                await Assert.That(context.Blocks[i].UvMode).IsEqualTo(reader.ReadByte());
                await Assert.That(context.Blocks[i].Segment).IsEqualTo(reader.ReadByte());
                await Assert.That(context.Blocks[i].Alpha).IsEqualTo(reader.ReadByte());
            }
            int predSize = context.PredStride * (4 * context.MbHeight + 1);
            await Assert.That(context.Modes[..predSize]).IsEquivalentTo(reader.ReadBytes(predSize), CollectionOrdering.Matching);
            for (int i = 0; i < 4; i++)
            {
                Vp8Segment segment = context.Segments[i];
                await Assert.That(segment.Alpha).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.Beta).IsEqualTo(reader.ReadInt32());
                await Assert.That(segment.Quant).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.FilterStrength).IsEqualTo(reader.ReadInt32());
                if (i >= context.SegmentCount) continue;
                await Assert.That(segment.LambdaI4).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.LambdaI16).IsEqualTo(reader.ReadInt32());
                await Assert.That(segment.LambdaUv).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.LambdaMode).IsEqualTo(reader.ReadInt32());
                await Assert.That(segment.LambdaTrellisI4).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.LambdaTrellisI16).IsEqualTo(reader.ReadInt32());
                await Assert.That(segment.LambdaTrellisUv).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.TLambda).IsEqualTo(reader.ReadInt32());
                await Assert.That(segment.MinDisto).IsEqualTo(reader.ReadInt32()); await Assert.That(segment.I4Penalty).IsEqualTo(reader.ReadInt32());
                await AssertMatrix(reader, segment.Y1); await AssertMatrix(reader, segment.Y2); await AssertMatrix(reader, segment.Uv);
            }
            await VerifyMacroblocks(reader, context, test);
        }
    }

    private static async Task VerifyMacroblocks(BinaryReader reader, Vp8EncoderContext context, int test)
    {
        var probabilities = new Vp8EncoderProba(); probabilities.CalculateLevelCosts();
        using var it = new Vp8EncoderIterator(context);
        do
        {
            it.Import();
            Vp8ModeScore rd = default;
            Vp8ModeDecision.Decimate(it, probabilities, ref rd, context.Config.RdLevel);
            string label = $"test={test}, method={context.Config.Options.Method}, q={context.Config.Options.Quality}, mb={it.X},{it.Y}";
            await Assert.That(reader.ReadUInt32() == rd.Nz).IsTrue().Because($"nz: {label}");
            await Assert.That(rd.D).IsEqualTo(reader.ReadInt64()); await Assert.That(rd.SD).IsEqualTo(reader.ReadInt64());
            await Assert.That(rd.R).IsEqualTo(reader.ReadInt64()); await Assert.That(rd.H).IsEqualTo(reader.ReadInt64()); await Assert.That(rd.Score).IsEqualTo(reader.ReadInt64());
            await Assert.That(it.Block.Type).IsEqualTo(reader.ReadByte()); await Assert.That(it.Block.UvMode).IsEqualTo(reader.ReadByte());
            await Assert.That(it.Block.Skip).IsEqualTo(reader.ReadByte() != 0);
            for (int y = 0; y < 4; y++) await Assert.That(context.Modes[(it.PredOffset + y * context.PredStride)..(it.PredOffset + y * context.PredStride + 4)]).IsEquivalentTo(reader.ReadBytes(4), CollectionOrdering.Matching);
            if (it.Block.Type == 1) await Assert.That(((ReadOnlySpan<short>)rd.Dc).ToArray()).IsEquivalentTo(ReadShorts(reader, 16), CollectionOrdering.Matching);
            await Assert.That(((ReadOnlySpan<short>)rd.Ac).ToArray()).IsEquivalentTo(ReadShorts(reader, 256), CollectionOrdering.Matching);
            await Assert.That(((ReadOnlySpan<short>)rd.Uv).ToArray()).IsEquivalentTo(ReadShorts(reader, 128), CollectionOrdering.Matching);
            for (int y = 0; y < 16; y++) await Assert.That(it.Output[(y * 32)..(y * 32 + 16)]).IsEquivalentTo(reader.ReadBytes(16), CollectionOrdering.Matching);
            for (int y = 0; y < 8; y++) await Assert.That(it.Output[(y * 32 + 16)..(y * 32 + 32)]).IsEquivalentTo(reader.ReadBytes(16), CollectionOrdering.Matching);
            uint dc = it.Block.Type == 1 ? rd.Nz & (1u << 24) : context.Nonzero[it.X + 1] & (1u << 24);
            context.Nonzero[it.X + 1] = rd.Nz | dc;
            if (it.Block.Type == 1) it.LeftNz[8] = (int)(rd.Nz >> 24) & 1;
            it.SaveBoundary();
        } while (it.Next());
    }

    private static async Task AssertMatrix(BinaryReader reader, Vp8Matrix matrix)
    {
        for (int i = 0; i < 16; i++)
        {
            await Assert.That((uint)(matrix.Q[i])).IsEqualTo(reader.ReadUInt32()); await Assert.That((uint)(matrix.Iq[i])).IsEqualTo(reader.ReadUInt32());
            await Assert.That(matrix.Bias[i]).IsEqualTo(reader.ReadUInt32()); await Assert.That(matrix.ZeroThreshold[i]).IsEqualTo(reader.ReadUInt32());
            await Assert.That((uint)(matrix.Sharpen[i])).IsEqualTo(reader.ReadUInt32());
        }
    }

    private static async Task AssertPlanes(BinaryReader reader, RgbToYuv yuv, string context)
    {
        int size = yuv.Width * yuv.Height, uvSize = yuv.UvWidth * yuv.UvHeight;
        await Assert.That(reader.ReadBytes(size).AsSpan().SequenceEqual(yuv.Y.AsSpan(0, size))).IsTrue().Because($"Y: {context}");
        await Assert.That(reader.ReadBytes(uvSize).AsSpan().SequenceEqual(yuv.U.AsSpan(0, uvSize))).IsTrue().Because($"U: {context}");
        await Assert.That(reader.ReadBytes(uvSize).AsSpan().SequenceEqual(yuv.V.AsSpan(0, uvSize))).IsTrue().Because($"V: {context}");
    }

    private static short[] ReadShorts(BinaryReader reader, int count)
    {
        var result = new short[count];
        for (int i = 0; i < count; i++) result[i] = reader.ReadInt16();
        return result;
    }
}
