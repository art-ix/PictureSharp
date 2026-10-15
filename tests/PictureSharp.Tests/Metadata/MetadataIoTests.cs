using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Metadata;

public class MetadataIoTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Files_AreReplacedOnlyAfterSuccess_AndTemporaryFilesAreCleaned(bool asynchronous)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = Corpus.Read("jpeg/progressive_420.jpg");
            string source = Path.Combine(directory, "source.jpg"), destination = Path.Combine(directory, "destination.jpg");
            File.WriteAllBytes(source, bytes);
            File.WriteAllBytes(destination, "existing-destination"u8.ToArray());
            var original = File.ReadAllBytes(destination);
            async Task Edit(string target, Action<ImageMetadata> action, CancellationToken token = default)
            {
                if (asynchronous) await Image.EditMetadataAsync(source, target, action, cancellationToken: token);
                else Image.EditMetadata(source, target, action, null, token);
            }
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Edit(destination, _ => throw new InvalidOperationException("callback")));
            await Sequence.AssertEqual(original, File.ReadAllBytes(destination));
            using var cts = new CancellationTokenSource();
            await Assert.ThrowsAsync<OperationCanceledException>(() => Edit(source, _ => cts.Cancel(), cts.Token));
            await Sequence.AssertEqual(bytes, File.ReadAllBytes(source));
            await Assert.ThrowsAsync<IOException>(() => Edit(directory, m => m.ExifProfile.SetValue(ExifTag.Model, "Failure")));
            await Sequence.AssertEqual(bytes, File.ReadAllBytes(source));
            await Assert.That(Directory.GetFiles(directory, ".picturesharp-*.tmp")).IsEmpty();
            await Edit(destination, m => m.ExifProfile.SetValue(ExifTag.Model, "Saved"));
            ImageMetadata metadata = asynchronous ? await Image.ReadMetadataAsync(destination) : Image.ReadMetadata(destination);
            metadata.ExifProfile.TryGetValue(ExifTag.Model, out var saved);
            await Assert.That(saved).IsEqualTo("Saved");
            await Edit(source, m => m.ExifProfile.SetValue(ExifTag.Model, "In place"));
            await Assert.That(Image.ReadMetadata(source).ExifProfile.TryGetValue(ExifTag.Model, out var inPlace)).IsTrue();
            await Assert.That(inPlace).IsEqualTo("In place");
            await Assert.That(Directory.GetFiles(directory, ".picturesharp-*.tmp")).IsEmpty();
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UnchangedEdit_LeavesInPlaceFileUntouched_AndCopiesToOtherPaths(bool asynchronous)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"picturesharp-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes = Corpus.Read("png/apng_alpha.png");
            string source = Path.Combine(directory, "source.png"), copy = Path.Combine(directory, "copy.png");
            File.WriteAllBytes(source, bytes);
            var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(source, stamp);
            // Removing absent GPS data is not a change.
            Action<ImageMetadata> edit = m => m.ExifProfile.RemoveGps();
            if (asynchronous) await Image.EditMetadataAsync(source, Path.Combine(directory, ".", "source.png"), edit);
            else Image.EditMetadata(source, source, edit);
            await Assert.That(File.GetLastWriteTimeUtc(source)).IsEqualTo(stamp);
            if (asynchronous) await Image.EditMetadataAsync(source, copy, edit);
            else Image.EditMetadata(source, copy, edit);
            await Sequence.AssertEqual(bytes, File.ReadAllBytes(copy));
            byte[] result = Image.EditMetadata(bytes, edit);
            await Assert.That(ReferenceEquals(result, bytes)).IsFalse();
            await Sequence.AssertEqual(bytes, result);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NonSeekableStreams_StayOpen_AndRespectInputLimits(bool asynchronous)
    {
        byte[] bytes = Corpus.Read("png/apng_alpha.png");
        using var input = new ForwardStream(bytes);
        using var destination = new MemoryStream();
        if (asynchronous) await Image.EditMetadataAsync(input, destination, m => m.ExifProfile.SetValue(ExifTag.Model, "Stream"));
        else Image.EditMetadata(input, destination, m => m.ExifProfile.SetValue(ExifTag.Model, "Stream"));
        using var read = new ForwardStream(destination.ToArray());
        ImageMetadata metadata = asynchronous ? await Image.ReadMetadataAsync(read) : Image.ReadMetadata(read);
        metadata.ExifProfile.TryGetValue(ExifTag.Model, out var model);
        await Assert.That(model).IsEqualTo("Stream");
        await Assert.That(input.CanRead && read.CanRead && destination.CanWrite).IsTrue();
        using var limited = new ForwardStream(bytes);
        var options = new DecoderOptions { MaxInputBytes = bytes.Length - 1 };
        if (asynchronous) await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Image.ReadMetadataAsync(limited, options));
        else await Assert.ThrowsExactlyAsync<ImageFormatException>(() => Task.Run(() => Image.ReadMetadata(limited, options)));
        await Assert.That(limited.CanRead).IsTrue();
    }

    [Test]
    public async Task OutputFailureAndCancellation_LeaveCallerStreamsOpen()
    {
        byte[] bytes = Corpus.Read("png/rgba8.png");
        foreach (bool cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            using var input = new ForwardStream(bytes);
            using var output = new FailingStream(cancel ? cts : null);
            if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => Image.EditMetadataAsync(input, output, _ => { }, cancellationToken: cts.Token));
            else await Assert.ThrowsExactlyAsync<IOException>(() => Image.EditMetadataAsync(input, output, _ => { }));
            await Assert.That(input.CanRead && output.CanWrite).IsTrue();
        }
    }

    private sealed class ForwardStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private sealed class FailingStream(CancellationTokenSource? cancellation) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (cancellation is null) throw new IOException("Synthetic output failure.");
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
