using System.Runtime.CompilerServices;
using PictureSharp.Domain.Frames;
using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

/// <summary>Current is borrowed until the next move or disposal; callers must Clone to retain a frame.</summary>
internal abstract class FrameReadSession(Stream input, FrameSequenceInfo info, DecodeContext context, bool leaveOpen)
    : FrameSession(input, info, context, leaveOpen)
{
    private FrameOwner Owner => new(this);
    private ImageFrame? _current;
    private int _count;
    private bool _complete;
    public ImageFrame Current
    {
        get { CheckAlive(); return _current ?? throw new InvalidOperationException("No current frame."); }
    }

    protected abstract ValueTask<ImageFrame?> ReadNextCoreAsync(CancellationToken cancellationToken);
    protected virtual void DisposeReader() { }

    public async ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        CancellationToken token = OperationToken(cancellationToken, out CancellationTokenSource? linked);
        try
        {
            ReleaseCurrent();
            token.ThrowIfCancellationRequested();
            if (_complete) return false;
            ImageFrame? frame = await ReadNextCoreAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (frame is null)
            {
                if (_count == 0 || (Info.FrameCount is { } expected && expected != _count))
                    throw new ImageFormatException(Info.Format, "The frame count does not match the sequence header.");
                _complete = true;
                return false;
            }
            if (frame.IsOwned || frame.Width != Info.Width || frame.Height != Info.Height)
                throw new ImageFormatException(Info.Format, "The reader returned an owned frame or inconsistent canvas.");
            if (Info.FrameCount is { } declared && _count >= declared)
                throw new ImageFormatException(Info.Format, "The sequence contains extra frames.");
            Context.Budget.ReserveImage(Info.Format, frame.Width, frame.Height);
            frame.Attach(Owner);
            _current = frame;
            _count++;
            return true;
        }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
        finally { linked?.Dispose(); End(); }
    }

    public async IAsyncEnumerable<ImageFrame> ReadFrames([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            while (await MoveNextAsync(cancellationToken).ConfigureAwait(false)) yield return Current;
        }
        finally { await DisposeAsync().ConfigureAwait(false); }
    }

    private void ReleaseCurrent()
    {
        if (_current is not null) { _current.Detach(Owner); _current = null; }
    }
    protected sealed override void DisposeState()
    {
        ReleaseCurrent();
        DisposeReader();
    }
}
