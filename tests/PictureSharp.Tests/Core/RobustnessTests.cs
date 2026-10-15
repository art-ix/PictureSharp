using PictureSharp.Processing;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Truncations and random mutations of every corpus file: decoders may only report
/// <see cref="ImageFormatException"/> or <see cref="NotSupportedException"/>.
/// </summary>
public class RobustnessTests
{
    // Small enough to keep mutated headers from allocating huge canvases, large enough for every corpus file.
    private static readonly DecoderOptions Options = new() { MaxPixels = 1 << 22 };

    public static TheoryData<string> CorpusFiles() => [.. CorpusPaths()];

    private static IEnumerable<string> CorpusPaths() =>
        Directory.EnumerateFiles(Corpus.CorpusDir, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is not ".bin")
            .Select(p => Path.GetRelativePath(Corpus.CorpusDir, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Truncations_OnlyThrowDocumentedExceptions(string file)
    {
        byte[] original = Corpus.Read(file);
        foreach (int length in TruncationLengths(original.Length))
        {
            AssertDecodesOrRejects(original.AsMemory(0, length), file, $"truncated to {length}");
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RandomMutations_OnlyThrowDocumentedExceptions(string file)
    {
        byte[] original = Corpus.Read(file);
        var rng = new Random(StableSeed(file));
        for (int iteration = 0; iteration < 120; iteration++)
        {
            byte[] data = Mutate(original, rng);
            AssertDecodesOrRejects(data, file, $"mutation {iteration}");
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void MaxPixels_IsEnforcedBeforeDecoding(string file)
    {
        byte[] data = Corpus.Read(file);
        ImageInfo info;
        try
        {
            info = Image.Identify(data);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            return;
        }

        long limit = (long)info.Width * info.Height - 1;
        if (limit < 1)
        {
            return;
        }

        var options = new DecoderOptions { MaxPixels = limit };
        Assert.Throws<ImageFormatException>(() => Image.Load(data, options).Dispose());
    }

    public static TheoryData<string> AnimatedCorpusFiles()
    {
        var data = new TheoryData<string>();
        foreach (string file in CorpusPaths())
        {
            try
            {
                if (Image.Identify(Corpus.Read(file)).FrameCount > 1)
                {
                    data.Add(file);
                }
            }
            catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
            {
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AnimatedCorpusFiles))]
    public void MaxPixels_CoversAllAnimationFrames(string file)
    {
        byte[] data = Corpus.Read(file);
        ImageInfo info = Image.Identify(data);
        long all = (long)info.Width * info.Height * info.FrameCount;

        Assert.Throws<ImageFormatException>(() => Image.Load(data, new DecoderOptions { MaxPixels = all - 1 }).Dispose());
        using (Image image = Image.Load(data, new DecoderOptions { MaxPixels = all }))
        {
            Assert.Equal(info.FrameCount, image.FrameCount);
        }

        using Image first = Image.Load(data, new DecoderOptions { MaxPixels = (long)info.Width * info.Height, FirstFrameOnly = true });
        Assert.Equal(1, first.FrameCount);
    }

    private static IEnumerable<int> TruncationLengths(int length)
    {
        var lengths = new SortedSet<int>();
        for (int i = 0; i < Math.Min(length, 96); i++)
        {
            lengths.Add(i);
        }

        for (int i = 1; i < 48; i++)
        {
            lengths.Add((int)((long)length * i / 48));
        }

        for (int i = 1; i <= 16 && i < length; i++)
        {
            lengths.Add(length - i);
        }

        lengths.Remove(length);
        return lengths;
    }

    private static byte[] Mutate(byte[] original, Random rng)
    {
        byte[] data = (byte[])original.Clone();
        if (data.Length == 0)
        {
            return data;
        }

        // Headers carry most of the structure, so half of the edits land in the first 64 bytes.
        int Position() => rng.Next(2) == 0 ? rng.Next(Math.Min(64, data.Length)) : rng.Next(data.Length);

        int edits = rng.Next(1, 6);
        for (int i = 0; i < edits; i++)
        {
            int pos = Position();
            switch (rng.Next(5))
            {
                case 0:
                    data[pos] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1:
                    data[pos] = (byte)rng.Next(256);
                    break;
                case 2:
                    data[pos] = rng.Next(2) == 0 ? (byte)0 : (byte)0xFF;
                    break;
                case 3:
                    // Typical length/dimension fields: overwrite a 16- or 32-bit word with an extreme value.
                    int size = rng.Next(2) == 0 ? 2 : 4;
                    uint value = rng.Next(3) switch { 0 => 0u, 1 => uint.MaxValue, _ => 0x7FFFFFFFu };
                    for (int b = 0; b < size && pos + b < data.Length; b++)
                    {
                        data[pos + b] = (byte)(value >> (8 * b));
                    }

                    break;
                default:
                    // Duplicate or drop a block, shifting everything after it.
                    int count = Math.Min(rng.Next(1, 33), data.Length - pos);
                    data = rng.Next(2) == 0
                        ? [.. data.AsSpan(0, pos), .. data.AsSpan(pos, count), .. data.AsSpan(pos)]
                        : [.. data.AsSpan(0, pos), .. data.AsSpan(pos + count)];
                    if (data.Length == 0)
                    {
                        return data;
                    }

                    break;
            }
        }

        return data;
    }

    private static void AssertDecodesOrRejects(ReadOnlyMemory<byte> data, string file, string variant)
    {
        try
        {
            Image.Identify(data);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
        }
        catch (Exception ex)
        {
            Assert.Fail($"{file} ({variant}): Identify threw {ex}");
        }

        try
        {
            using Image image = Image.Load(data, Options);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
        }
        catch (Exception ex)
        {
            Assert.Fail($"{file} ({variant}): Load threw {ex}");
        }
    }

    private static int StableSeed(string text)
    {
        int hash = 17;
        foreach (char c in text)
        {
            hash = unchecked((hash * 31) + c);
        }

        return hash;
    }
}
