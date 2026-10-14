using System.Buffers;

namespace RasterSharp.Internal;

/// <summary>
/// Write-only stream over pooled segments. Unlike <see cref="MemoryStream"/>, growing never copies, and
/// <see cref="ToArray"/> makes the only exact-size copy, so encoding to a byte array allocates about the output size.
/// </summary>
internal sealed class PooledBufferStream : Stream
{
    private const int FirstSegmentSize = 16 * 1024;
    private const int MaxSegmentSize = 1024 * 1024;

    private readonly List<byte[]> _segments = [];
    private int _used;
    private long _length;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_segments.Count == 0 || _used == _segments[^1].Length)
            {
                // Segments double up to 1 MiB, so small outputs stay small and large ones need few segments.
                int size = _segments.Count == 0 ? FirstSegmentSize : Math.Min(MaxSegmentSize, _segments[^1].Length * 2);
                _segments.Add(ArrayPool<byte>.Shared.Rent(size));
                _used = 0;
            }

            byte[] segment = _segments[^1];
            int count = Math.Min(buffer.Length, segment.Length - _used);
            buffer[..count].CopyTo(segment.AsSpan(_used));
            _used += count;
            _length += count;
            buffer = buffer[count..];
        }
    }

    public override void WriteByte(byte value) => Write([value]);

    public byte[] ToArray()
    {
        byte[] result = GC.AllocateUninitializedArray<byte>(checked((int)_length));
        int position = 0;
        for (int i = 0; i < _segments.Count; i++)
        {
            int count = i == _segments.Count - 1 ? _used : _segments[i].Length;
            _segments[i].AsSpan(0, count).CopyTo(result.AsSpan(position));
            position += count;
        }

        return result;
    }

    public void WriteTo(Stream destination)
    {
        for (int i = 0; i < _segments.Count; i++)
        {
            destination.Write(_segments[i], 0, i == _segments.Count - 1 ? _used : _segments[i].Length);
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        foreach (byte[] segment in _segments)
        {
            ArrayPool<byte>.Shared.Return(segment);
        }

        _segments.Clear();
        base.Dispose(disposing);
    }
}
