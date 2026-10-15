using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

/// <summary>A session owns its codec state, and owns the stream only when leaveOpen is false.</summary>
internal abstract class FrameSession(Stream stream, FrameSequenceInfo info, DecodeContext context, bool leaveOpen) : IDisposable, IAsyncDisposable
{
    private bool _disposed;
    private int _busy;
    protected Stream Stream { get; } = stream;
    protected DecodeContext Context { get; } = context;
    public FrameSequenceInfo Info { get; } = info;

    protected void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent session operations are not supported.");
    }
    protected void End() => Volatile.Write(ref _busy, 0);
    protected void CheckAlive() => ObjectDisposedException.ThrowIf(_disposed, this);

    protected CancellationToken OperationToken(CancellationToken token, out CancellationTokenSource? linked)
    {
        linked = null;
        CancellationToken original = Context.CancellationToken;
        if (original.CanBeCanceled && token.CanBeCanceled && original != token)
        {
            linked = CancellationTokenSource.CreateLinkedTokenSource(original, token);
            return linked.Token;
        }
        return token.CanBeCanceled ? token : original;
    }

    protected virtual void DisposeState() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisposeState(); }
        finally { if (!leaveOpen) Stream.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { DisposeState(); }
        finally { if (!leaveOpen) await Stream.DisposeAsync().ConfigureAwait(false); }
    }

    protected async ValueTask AbortAsync()
    {
        // Preserve the original read/write/format error if cleanup also fails.
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { }
    }
}
