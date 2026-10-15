using System.Drawing;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Sequences;
using PictureSharp.Composition;
using PictureSharp.Infrastructure.IO;

namespace PictureSharp.Tests.Sequences;

public class FrameWriterCleanupTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TemporaryOutput_IsDisposedOnCompletionAbandonmentAndCopyFailure(bool cancel)
    {
        foreach (string action in new[] { "complete", "abandon", "write-failure", "copy-failure" })
        {
            var files = new TrackingOutput();
            var service = new ImageSequenceService(new ImageInput(), files, LibraryServices.Codecs);
            using var cts = new CancellationTokenSource();
            using var destination = new ForwardOutput(action == "copy-failure", cancel ? cts : null);
            await using var writer = service.CreateWriter(destination, new Size(2, 2), service.DefaultOptions(ImageFormat.WebP),
                new ImageFrameWriterOptions { UseTemporaryFile = true });
            await writer.WriteAsync(new ImageFrame(2, 2));
            await Assert.That(destination.BytesWritten).IsEqualTo(0L);
            switch (action)
            {
                case "complete":
                    await writer.CompleteAsync();
                    await Assert.That(destination.BytesWritten > 0).IsTrue();
                    break;
                case "write-failure":
                    await Assert.ThrowsExactlyAsync<ArgumentException>(async () => await writer.WriteAsync(new ImageFrame(3, 2)));
                    break;
                case "copy-failure":
                    if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.CompleteAsync(cts.Token));
                    else await Assert.ThrowsExactlyAsync<IOException>(async () => await writer.CompleteAsync());
                    break;
                default: await writer.DisposeAsync(); break;
            }
            await Assert.That(files.Temporary.IsDisposed).IsTrue();
            await Assert.That(destination.CanWrite).IsTrue();
        }
    }

    private sealed class TrackingOutput : IImageOutput
    {
        public TrackedStream Temporary { get; } = new();
        public Stream CreateTemporaryFile() => Temporary;
        public Stream CreateFile(string path) => throw new NotSupportedException();
        public void DeleteFile(string path) => throw new NotSupportedException();
        public void WriteFile(string path, byte[] data) => throw new NotSupportedException();
        public Task WriteFileAsync(string path, byte[] data, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TrackedStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }
        protected override void Dispose(bool disposing) { IsDisposed = true; base.Dispose(disposing); }
    }

    private sealed class ForwardOutput(bool fail, CancellationTokenSource? cancel) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fail)
            {
                if (cancel is null) throw new IOException("test copy failure");
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
