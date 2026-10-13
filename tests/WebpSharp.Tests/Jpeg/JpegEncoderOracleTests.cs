using WebpSharp.Tests.Infrastructure;

namespace WebpSharp.Tests.Jpeg;

public class JpegEncoderOracleTests
{
    [Fact]
    [Trait("Category", "Oracle")]
    public void AllSamplingAndRestartModes_AreByteExactWithLibjpeg_AndOpenInPillowAndImageMagick()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"webpsharp-jpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string oracle = Path.Combine(directory, "oracle");
            string source = Path.Combine(Corpus.Assets, "jpeg_encode_oracle.c");
            if (ReferenceTools.Run("cc", $"\"{source}\" -ljpeg -o \"{oracle}\"", out _) != 0) return;
            string raw = Path.Combine(directory, "input.rgb"), expectedPath = Path.Combine(directory, "expected.jpg");
            string actualPath = Path.Combine(directory, "actual.jpg"), rgba = Path.Combine(directory, "decoded.rgba");
            string script = Path.Combine(directory, "decode.py");
            File.WriteAllText(script, "from PIL import Image\nimport sys\nopen(sys.argv[2], 'wb').write(Image.open(sys.argv[1]).convert('RGBA').tobytes())\n");
            bool pillow = ReferenceTools.Run("python3", "-I -c \"from PIL import Image\"", out _) == 0;
            bool convert = ReferenceTools.Run("convert", "-version", out _) == 0;
            var random = new Random(5515);
            foreach ((int width, int height, bool gray) in new[] { (1, 1, false), (9, 17, false), (17, 9, false), (73, 51, false), (257, 129, true), (512, 257, false), (2048, 1033, true) })
            {
                using var image = new Image(width, height);
                foreach (ref Rgba32 p in image.GetPixelSpan())
                {
                    byte r = (byte)random.Next(256);
                    p = new Rgba32(r, gray ? r : (byte)random.Next(256), gray ? r : (byte)random.Next(256), 255);
                }

                if (width == 2048)
                {
                    image.GetPixelSpan().Fill(new Rgba32(128, 128, 128, 255));
                    image[0, 0] = new Rgba32(0, 0, 0, 255);
                }

                File.WriteAllBytes(raw, image.ToPixelArray(gray ? PixelFormat.Gray8 : PixelFormat.Rgb24));
                foreach (JpegSubsampling sampling in Enum.GetValues<JpegSubsampling>())
                foreach (int interval in new[] { 0, 1, 7, 65535 })
                foreach ((bool optimize, bool progressive) in new[] { (false, false), (true, false), (false, true) })
                {
                    if (width == 2048 && (sampling != JpegSubsampling.Ratio420 || !progressive || interval is not (0 or 7))) continue;
                    var options = new JpegEncoderOptions { Subsampling = sampling, RestartInterval = interval, OptimizeCoding = optimize, Progressive = progressive, Quality = 90 };
                    byte[] encoded = image.ToJpeg(options);
                    Assert.Equal(0, ReferenceTools.Run(oracle, $"\"{raw}\" \"{expectedPath}\" {width} {height} 90 {(int)sampling} {(optimize ? 1 : 0)} {(progressive ? 1 : 0)} {interval} {(gray ? 1 : 0)}", out _));
                    Assert.True(File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(encoded), $"{width}x{height} gray={gray}, {sampling}, restart={interval}, optimize={optimize}, progressive={progressive}");
                    File.WriteAllBytes(actualPath, encoded);
                    using Image decoded = Image.FromBytes(encoded.AsMemory());
                    if (pillow)
                    {
                        Assert.Equal(0, ReferenceTools.Run("python3", $"-I \"{script}\" \"{actualPath}\" \"{rgba}\"", out _));
                        Assert.Equal(File.ReadAllBytes(rgba), decoded.ToPixelArray(PixelFormat.Rgba32));
                    }
                    if (convert) Assert.Equal(0, ReferenceTools.Run("convert", $"\"{actualPath}\" null:", out _));
                }
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
