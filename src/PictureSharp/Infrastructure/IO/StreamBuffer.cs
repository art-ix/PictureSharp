using System.Buffers;
using PictureSharp.Application.Abstractions;

namespace PictureSharp.Infrastructure.IO;

/// <summary>
/// Reads whole inputs into memory without buffering more than <see cref="DecoderOptions.MaxInputBytes"/>. A declared
/// stream length only sizes the first buffer; the limit is enforced on the bytes actually read.
/// </summary>
internal static class StreamBuffer
{
    private const int MinimumGrowth = 64 * 1024;

    public static byte[] ReadFile(string path, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0);
        return ReadAll(file, maxBytes, cancellationToken);
    }

    public static async Task<byte[]> ReadFileAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);
        return await ReadAllAsync(file, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] ReadAll(Stream stream, long maxBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is MemoryStream memoryStream && memoryStream.TryGetBuffer(out ArraySegment<byte> segment))
        {
            ReadOnlySpan<byte> rest = segment.AsSpan((int)Math.Min(memoryStream.Position, segment.Count));
            CheckLength(rest.Length, maxBytes);
            byte[] copy = rest.ToArray();
            memoryStream.Position = memoryStream.Length;
            return copy;
        }

        using var buffer = new Buffer(InitialCapacity(stream, maxBytes), maxBytes);
        Span<byte> probe = stackalloc byte[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.IsFull)
            {
                // Probe one byte before growing, so an exactly sized first buffer is never reallocated.
                if (stream.Read(probe) == 0)
                {
                    return buffer.ToArray();
                }

                buffer.Append(probe[0]);
            }

            int read = stream.Read(buffer.Free);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            buffer.Advance(read);
        }
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream is MemoryStream)
        {
            return ReadAll(stream, maxBytes, cancellationToken);
        }

        using var buffer = new Buffer(InitialCapacity(stream, maxBytes), maxBytes);
        byte[] probe = new byte[1];
        while (true)
        {
            if (buffer.IsFull)
            {
                if (await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) == 0)
                {
                    return buffer.ToArray();
                }

                buffer.Append(probe[0]);
            }

            int read = await stream.ReadAsync(buffer.FreeMemory, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            buffer.Advance(read);
        }
    }

    public static void CheckLength(long length, long maxBytes) => InputLimits.CheckLength(length, maxBytes);

    private static int InitialCapacity(Stream stream, long maxBytes)
    {
        if (!stream.CanSeek)
        {
            return (int)Math.Min(MinimumGrowth, maxBytes);
        }

        long declared = Math.Max(stream.Length - stream.Position, 0);
        CheckLength(declared, maxBytes);
        return (int)Math.Min(declared, Array.MaxLength);
    }

    /// <summary>
    /// A growable byte buffer that refuses to hold more than the limit. A buffer sized from a declared length is
    /// allocated exactly and returned as is; a growing buffer lives in pooled arrays until one exact-size copy.
    /// </summary>
    private sealed class Buffer(int capacity, long maxBytes) : IDisposable
    {
        private readonly long _limit = Math.Min(maxBytes, Array.MaxLength);
        private byte[] _data = capacity == 0 ? [] : GC.AllocateUninitializedArray<byte>(capacity);
        private bool _pooled;
        private int _capacity = capacity;
        private int _length;

        public bool IsFull => _length == _capacity;

        public Span<byte> Free => _data.AsSpan(_length, _capacity - _length);

        public Memory<byte> FreeMemory => _data.AsMemory(_length, _capacity - _length);

        public void Advance(int count) => _length += count;

        public void Append(byte value)
        {
            CheckLength(_length + 1L, _limit);
            int size = (int)Math.Min(Math.Max(_length * 2L, _length + (long)MinimumGrowth), _limit);
            byte[] next = ArrayPool<byte>.Shared.Rent(size);
            _data.AsSpan(0, _length).CopyTo(next);
            ReturnPooled();
            (_data, _pooled, _capacity) = (next, true, size);
            _data[_length++] = value;
        }

        public byte[] ToArray() => !_pooled && _length == _data.Length ? _data : _data.AsSpan(0, _length).ToArray();

        public void Dispose()
        {
            ReturnPooled();
            _data = [];
        }

        private void ReturnPooled()
        {
            if (_pooled)
            {
                ArrayPool<byte>.Shared.Return(_data);
                _pooled = false;
            }
        }
    }
}
