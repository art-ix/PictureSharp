using PictureSharp.Application.Decoding;

namespace PictureSharp.Infrastructure.IO;

/// <summary>Forward-only input charged to the same operation budget as decoded frames and metadata.</summary>
internal sealed class BudgetedReadStream(Stream input, DecodeContext context, bool leaveOpen) : Stream
{
    private bool _disposed;
    public override bool CanRead => !_disposed && input.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    private int ReadLength(int requested) => (int)Math.Min(requested, Math.Min(int.MaxValue, context.Budget.RemainingInput) + 1L);
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        context.CancellationToken.ThrowIfCancellationRequested();
        int read = input.Read(buffer[..ReadLength(buffer.Length)]);
        context.Budget.ReserveInput(read);
        return read;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        context.CancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource? linked = context.CancellationToken.CanBeCanceled && cancellationToken.CanBeCanceled && context.CancellationToken != cancellationToken
            ? CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, cancellationToken) : null;
        CancellationToken token = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : context.CancellationToken);
        int read = await input.ReadAsync(buffer[..ReadLength(buffer.Length)], token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        context.Budget.ReserveInput(read);
        return read;
    }
    protected override void Dispose(bool disposing) { if (!_disposed && disposing && !leaveOpen) input.Dispose(); _disposed = true; base.Dispose(disposing); }
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!leaveOpen) await input.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
