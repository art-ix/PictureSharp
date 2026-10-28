using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Sequences;

public class FrameReaderTests
{
    /// <summary>Every GIF, PNG and WebP file of the corpus, including still images and files the decoder rejects.</summary>
    public static IEnumerable<string> SequentialFiles() =>
        new[] { "gif", "png", "webp" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(Corpus.CorpusDir, dir), "*", SearchOption.AllDirectories))
            .Where(path => Path.GetExtension(path) is ".gif" or ".png" or ".webp")
            .Select(path => Path.GetRelativePath(Corpus.CorpusDir, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Test]
    [MethodDataSource(nameof(SequentialFiles))]
    public async Task Reader_MatchesLoad_FromFileAndNonSeekableStream(string file)
    {
        byte[] data = Corpus.Read(file);
        Image? expected = null;
        Exception? loadError = null;
        try
        {
            expected = Image.Load(data);
        }
        catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
        {
            loadError = ex;
        }

        using (expected)
        {
            foreach (bool fromFile in new[] { true, false })
            {
                var frames = new List<ImageFrame>();
                Exception? readError = null;
                ImageFrameReader? reader = null;
                try
                {
                    reader = fromFile
                        ? await Image.OpenFrameReaderAsync(Corpus.PathOf(file))
                        : await Image.OpenFrameReaderAsync(new NonSeekableStream(data, 977));
                    while (await reader.MoveNextAsync())
                    {
                        frames.Add(reader.Current.Clone());
                    }
                }
                catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
                {
                    readError = ex;
                }

                if (expected is null)
                {
                    // Errors the decoder reports for the whole file are reported by the reader too (possibly after some frames).
                    await Assert.That(readError?.GetType()).IsEqualTo(loadError!.GetType()).Because(readError?.Message ?? "no error");
                    reader?.Dispose();
                    continue;
                }

                await Assert.That(readError).IsNull().Because(readError?.ToString() ?? "");
                await Assert.That(reader!.IsComplete).IsTrue();
                await Assert.That(frames.Count).IsEqualTo(expected.FrameCount);
                await Assert.That((reader.Width, reader.Height)).IsEqualTo((expected.Width, expected.Height));
                await Assert.That(reader.LoopCount).IsEqualTo(expected.LoopCount);
                await Assert.That(reader.AnimationBackgroundColor).IsEqualTo(expected.AnimationBackgroundColor);
                await Assert.That(reader.Format).IsEqualTo(expected.SourceFormat);
                for (int i = 0; i < frames.Count; i++)
                {
                    await Assert.That(frames[i].Duration).IsEqualTo(expected.Frames[i].Duration);
                    await Assert.That(frames[i].GetPixelSpan<Rgba32>().SequenceEqual(expected.Frames[i].GetPixelSpan<Rgba32>())).IsTrue().Because($"frame {i}");
                }

                ImageMetadata a = reader.Metadata, b = expected.Metadata;
                await Sequence.AssertEqual(b.IccProfile, a.IccProfile);
                await Sequence.AssertEqual(b.Exif, a.Exif);
                await Sequence.AssertEqual(b.Xmp, a.Xmp);
                await Assert.That((a.HorizontalResolution, a.VerticalResolution)).IsEqualTo((b.HorizontalResolution, b.VerticalResolution));
                await reader.DisposeAsync();
            }
        }
    }
}
