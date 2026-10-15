using System.Buffers;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Infrastructure.IO;

/// <summary>
/// Buffered forward-only reads for sequential codecs. Small reads are served from a pooled buffer, so the block and
/// chunk parsers can read headers byte by byte without an asynchronous call per byte. The underlying stream may be
/// read ahead by up to one buffer.
/// </summary>
internal sealed class ForwardReader(Stream stream) : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _start;
    private int _end;
    private bool _endOfStream;

    /// <summary>Number of bytes consumed so far.</summary>
    public long Position { get; private set; }

    /// <summary>Reads one byte, or returns -1 at the end of the stream.</summary>
    public ValueTask<int> ReadByteAsync(CancellationToken cancellationToken)
    {
        if (_start < _end)
        {
            Position++;
            return new(Buffer[_start++]);
        }

        return ReadByteSlowAsync(cancellationToken);
    }

    private async ValueTask<int> ReadByteSlowAsync(CancellationToken cancellationToken)
    {
        if (!await FillAsync(cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        Position++;
        return Buffer[_start++];
    }

    /// <summary>Fills <paramref name="destination"/> and returns its length, or fewer bytes at the end of the stream.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            if (_start == _end)
            {
                // Large reads bypass the buffer.
                if (destination.Length - total >= BufferSize && !_endOfStream)
                {
                    int direct = await stream.ReadAtLeastAsync(destination[total..], destination.Length - total, throwOnEndOfStream: false,
                        cancellationToken).ConfigureAwait(false);
                    _endOfStream = direct < destination.Length - total;
                    total += direct;
                    Position += direct;
                    break;
                }

                if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }

            int count = Math.Min(_end - _start, destination.Length - total);
            Buffer.AsSpan(_start, count).CopyTo(destination.Span[total..]);
            _start += count;
            total += count;
            Position += count;
        }

        return total;
    }

    /// <summary>
    /// Appends the next <paramref name="count"/> bytes to <paramref name="target"/> and returns how many were read. The
    /// buffer grows with the data that actually arrives, so a forged length cannot allocate more than the input holds.
    /// </summary>
    public async ValueTask<long> ReadToAsync(PooledByteBuffer target, long count, CancellationToken cancellationToken)
    {
        long done = 0;
        while (done < count)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            // Grow only after bytes actually arrive and pass the input budget, never from a forged chunk length.
            int step = (int)Math.Min(_end - _start, count - done);
            target.Append(Buffer.AsSpan(_start, step));
            _start += step;
            Position += step;
            done += step;
        }

        return done;
    }

    /// <summary>Skips <paramref name="count"/> bytes and returns how many were skipped (fewer at the end of the stream).</summary>
    public async ValueTask<long> SkipAsync(long count, CancellationToken cancellationToken)
    {
        long skipped = 0;
        while (skipped < count)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            int step = (int)Math.Min(_end - _start, count - skipped);
            _start += step;
            skipped += step;
            Position += step;
        }

        return skipped;
    }

    /// <summary>Calls <paramref name="consume"/> for consecutive pieces of the next <paramref name="count"/> bytes.</summary>
    public async ValueTask<long> ConsumeAsync(long count, Action<ReadOnlyMemory<byte>> consume, CancellationToken cancellationToken)
    {
        long done = 0;
        while (done < count)
        {
            if (_start == _end && !await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            int step = (int)Math.Min(_end - _start, count - done);
            consume(Buffer.AsMemory(_start, step));
            _start += step;
            done += step;
            Position += step;
        }

        return done;
    }

    private byte[] Buffer => _buffer ?? throw new ObjectDisposedException(nameof(ForwardReader));

    private async ValueTask<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_endOfStream)
        {
            return false;
        }

        _start = 0;
        _end = await stream.ReadAsync(Buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        _endOfStream = _end == 0;
        return _end > 0;
    }

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
