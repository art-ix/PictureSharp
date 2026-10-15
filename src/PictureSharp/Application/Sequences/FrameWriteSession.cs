using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

/// <summary>Writes borrowed frames in order. Only CompleteAsync writes a successful sequence footer.</summary>
internal abstract class FrameWriteSession(Stream output, FrameSequenceInfo info, DecodeContext context, bool leaveOpen)
    : FrameSession(output, info, context, leaveOpen)
{
    private int _count;
    private bool _complete;

    /// <summary>Frames accepted so far.</summary>
    public int FramesWritten => _count;

    /// <summary>Whether the sequence footer was written.</summary>
    public bool IsComplete => _complete;
    /// <summary>Encodes a borrowed frame; <paramref name="duration"/> replaces <see cref="ImageFrame.Duration"/>.</summary>
    protected abstract ValueTask WriteCoreAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken);
    protected abstract ValueTask CompleteCoreAsync(CancellationToken cancellationToken);

    public ValueTask WriteAsync(ImageFrame frame, CancellationToken cancellationToken = default) =>
        WriteAsync(frame, frame?.Duration ?? TimeSpan.Zero, cancellationToken);

    public async ValueTask WriteAsync(ImageFrame frame, TimeSpan duration,CancellationToken cancellationToken = default)
    {
        Begin();
        CancellationToken token = OperationToken(cancellationToken, out CancellationTokenSource? linked);
        try
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            token.ThrowIfCancellationRequested();
            if (_complete) throw new InvalidOperationException("The writer is already complete.");
            if (frame.Width != Info.Width || frame.Height != Info.Height)
                throw new ArgumentException("Frame dimensions must match the sequence canvas.", nameof(frame));
            if (Info.FrameCount is { } declared && _count >= declared)
                throw new ImageFormatException(Info.Format, "Too many frames for the declared sequence.");
            Context.Budget.ReserveImage(Info.Format, frame.Width, frame.Height);
            await WriteCoreAsync(frame, duration, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _count++;
        }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
        finally { linked?.Dispose(); End(); }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        CancellationToken token = OperationToken(cancellationToken, out CancellationTokenSource? linked);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_complete) return;
            if (_count == 0 || (Info.FrameCount is { } declared && _count != declared))
                throw new ImageFormatException(Info.Format, "The written frame count does not match the declaration.");
            await CompleteCoreAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _complete = true;
        }
        catch { await AbortAsync().ConfigureAwait(false); throw; }
        finally { linked?.Dispose(); End(); }
    }
}
