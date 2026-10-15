using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Sequences;

namespace PictureSharp;

/// <summary>Encodes full-canvas frames one at a time. Create one with <c>Image.CreateFrameWriter</c>.</summary>
/// <remarks>
/// Frames and images are borrowed only during <c>WriteAsync</c>; afterwards they may be changed or reused.
/// Call <see cref="CompleteAsync"/> to finish the file. Disposal without completion aborts the sequence and
/// deletes a path output. Caller streams stay open and may contain partial output after an error or cancellation.
/// Operations are not thread-safe. Encoder CPU work runs on the calling thread.
/// </remarks>
public sealed class ImageFrameWriter : IDisposable, IAsyncDisposable
{
    private readonly FrameWriteSession _session;
    private readonly Stream _encoded;
    private readonly Stream? _destination;
    private readonly IImageOutput _output;
    private readonly string? _path;
    private readonly bool _ownsStream;
    private bool _disposed;
    private int _busy;

    internal ImageFrameWriter(FrameWriteSession session, Stream encoded, Stream? destination,
        IImageOutput output, string? path, bool ownsStream)
    {
        _session = session;
        _encoded = encoded;
        _destination = destination;
        _output = output;
        _path = path;
        _ownsStream = ownsStream;
    }

    /// <summary>The output format.</summary>
    public ImageFormat Format => _session.Info.Format;

    /// <summary>Canvas width in pixels; every frame must have this width.</summary>
    public int Width => _session.Info.Width;

    /// <summary>Canvas height in pixels; every frame must have this height.</summary>
    public int Height => _session.Info.Height;

    /// <summary>The common canvas size required of every frame.</summary>
    public System.Drawing.Size Size => new(Width, Height);

    /// <summary>Number of frames accepted so far.</summary>
    public int FramesWritten => _session.FramesWritten;

    /// <summary>Whether headers, the footer and any temporary-file copy were successfully completed.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Encodes a frame with its duration. The frame must match <see cref="Size"/> and remains caller-owned.</summary>
    /// <remarks>A failed or canceled write aborts the writer; it cannot be resumed.</remarks>
    public ValueTask WriteAsync(ImageFrame frame, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(frame, frame?.Duration ?? TimeSpan.Zero, cancellationToken);

    /// <summary>
    /// Encodes a frame with the given duration instead of <see cref="ImageFrame.Duration"/>, which is left unchanged.
    /// The frame must match <see cref="Size"/> and remains caller-owned.
    /// </summary>
    /// <remarks>A failed or canceled write aborts the writer; it cannot be resumed.</remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public ValueTask WriteAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken = default) =>
        WriteCoreAsync(frame, duration, cancellationToken);

    /// <summary>
    /// Encodes the root frame of <paramref name="image"/> with the given duration, like
    /// <see cref="ImageFrameCollection.Add(Image, TimeSpan)"/>. The image must match <see cref="Size"/> and remains caller-owned.
    /// </summary>
    /// <remarks>
    /// Use it to write frames processed with <c>Mutate</c>, for example images from
    /// <see cref="ImageFrameReader.CloneCurrent"/>. A failed or canceled write aborts the writer; it cannot be resumed.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="duration"/> is negative.</exception>
    public ValueTask WriteAsync(Image image, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return WriteCoreAsync(image.Frames.RootFrame, duration, cancellationToken);
    }

    private async ValueTask WriteCoreAsync(ImageFrame frame, TimeSpan duration, CancellationToken cancellationToken)
    {
        Begin();
        try
        {
            await _session.WriteAsync(frame, duration, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>Checks the declared frame count, finishes the file and flushes the output. At least one frame is required.</summary>
    /// <remarks>When a temporary file is used, copying it to the caller stream happens here. Repeated completion is harmless.</remarks>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Begin();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsComplete) return;
            await _session.CompleteAsync(cancellationToken).ConfigureAwait(false);
            if (_destination is not null)
            {
                _encoded.Position = 0;
                await _encoded.CopyToAsync(_destination, cancellationToken).ConfigureAwait(false);
                await _destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _encoded.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            IsComplete = true;
            // Close owned files now, so the completed path can be opened before disposing the writer.
            await _session.DisposeAsync().ConfigureAwait(false);
            if (_ownsStream) await _encoded.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            IsComplete = false;
            await AbortAsync().ConfigureAwait(false);
            throw;
        }
        finally { Volatile.Write(ref _busy, 0); }
    }

    private void Begin()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Concurrent writer operations are not supported.");
    }

    private async ValueTask AbortAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch { } // Cleanup must not replace the original encoding or I/O error.
    }

    /// <summary>Releases buffers and owned files; aborts an incomplete sequence. Caller streams stay open.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _session.Dispose(); }
        finally
        {
            try { if (_ownsStream) _encoded.Dispose(); }
            finally { if (!IsComplete && _path is not null) _output.DeleteFile(_path); }
        }
    }

    /// <summary>Releases buffers and owned files asynchronously; aborts an incomplete sequence.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (_ownsStream) await _encoded.DisposeAsync().ConfigureAwait(false); }
            finally { if (!IsComplete && _path is not null) _output.DeleteFile(_path); }
        }
    }
}
