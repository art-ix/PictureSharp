using System.Runtime.InteropServices;
using System.Text.Json;
using PictureSharp.Infrastructure.Codecs.Jpeg;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Jpeg;

/// <summary>
/// Real camera photos from samples/ (private, not committed) compared with Pillow at run time
/// (tests/assets/jpeg_reference.py). Produces no cases when there are no samples or no Pillow.
/// </summary>
[Property("Category", "Local")]
public class JpegSamplesTests
{
    private static readonly Dictionary<int, Dictionary<string, string>?> ScaledReferences = [];

    private static readonly Lazy<Dictionary<string, string>?> Reference = new(() => PillowReference(Corpus.SamplesDir, null));

    /// <summary>Runs jpeg_reference.py over <paramref name="dir"/>; null when the directory or Pillow is missing.</summary>
    internal static Dictionary<string, string>? PillowReference(string dir, string? dumpDir, int scale = 1)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        string script = Path.Combine(Corpus.Assets, "jpeg_reference.py");
        string args = $"-I \"{script}\" --scale {scale} \"{dir}\"" + (dumpDir is null ? "" : $" \"{dumpDir}\"");
        if (ReferenceTools.Run("python3", args, out string json) != 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(json[..(json.LastIndexOf('}') + 1)]);
    }

    public static TestCases<string> Files()
    {
        var data = new TestCases<string>();
        foreach (string name in Reference.Value is { Count: > 0 } reference ? reference.Keys : (IEnumerable<string>)[""])
        {
            data.Add(name);
        }

        return data;
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Sample_IsBitExactWithPillow(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        string expected = Reference.Value![name];
        await Assert.That(expected).IsNotEqualTo("error");
        using Image image = Image.Load(Path.Combine(Corpus.SamplesDir, name));
        await Assert.That(expected == Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan<Rgba32>()))).IsTrue().Because($"{name} differs from Pillow");
    }

    [Test]
    [MethodDataSource(nameof(Files))]
    public async Task Sample_ScaledDecode_IsBitExactWithPillow(string name)
    {
        if (name.Length == 0)
        {
            return;
        }

        foreach (int scale in (int[])[2, 4, 8])
        {
            string expected = ScaledReference(scale)![name];
            using Image image = JpegDecoder.DecodeScaled(File.ReadAllBytes(Path.Combine(Corpus.SamplesDir, name)), DecoderOptions.Default, scale);
            await Assert.That(expected == Corpus.Sha256(MemoryMarshal.AsBytes(image.GetPixelSpan<Rgba32>()))).IsTrue().Because($"{name} at 1/{scale} differs from Pillow");
        }
    }

    private static Dictionary<string, string>? ScaledReference(int scale)
    {
        lock (ScaledReferences)
        {
            if (!ScaledReferences.TryGetValue(scale, out Dictionary<string, string>? reference))
            {
                ScaledReferences[scale] = reference = PillowReference(Corpus.SamplesDir, null, scale);
            }

            return reference;
        }
    }
}
