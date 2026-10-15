using System.Buffers;

namespace PictureSharp.Primitives.Memory;

/// <summary>A contiguous growable byte buffer rented from the shared pool and reused across frames.</summary>
internal sealed class PooledByteBuffer : IDisposable
{
    private byte[] _array = [];

    public int Length { get; private set; }

    /// <summary>The whole rented array; only the first <see cref="Length"/> bytes are valid.</summary>
    public byte[] Array => _array;

    public ReadOnlyMemory<byte> Memory => _array.AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => _array.AsSpan(0, Length);

    public void Clear() => Length = 0;

    /// <summary>Drops the bytes after <paramref name="length"/>.</summary>
    public void Truncate(int length) => Length = Math.Min(Length, length);

    /// <summary>Makes room for <paramref name="count"/> more bytes and returns the region to fill; call <see cref="Advance"/> after filling it.</summary>
    public Memory<byte> GetMemory(int count)
    {
        int required = checked(Length + count);
        if (required > _array.Length)
        {
            byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Max(required, Math.Min(System.Array.MaxLength, Math.Max(4096L, _array.Length * 2L))));
            _array.AsSpan(0, Length).CopyTo(larger);
            Return();
            _array = larger;
        }

        return _array.AsMemory(Length, count);
    }

    public void Advance(int count) => Length += count;

    public void Append(ReadOnlySpan<byte> data)
    {
        data.CopyTo(GetMemory(data.Length).Span);
        Length += data.Length;
    }

    public void Append(byte value) => Append([value]);

    public byte[] ToArray() => Span.ToArray();

    private void Return()
    {
        if (_array.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_array);
        }
    }

    public void Dispose()
    {
        Return();
        _array = [];
        Length = 0;
    }
}
