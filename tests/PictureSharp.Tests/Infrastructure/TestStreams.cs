namespace PictureSharp.Tests.Infrastructure;

/// <summary>A read-only stream that cannot seek, returns short reads and counts the bytes it hands out.</summary>
public sealed class NonSeekableStream(byte[] data, int maxRead = 4093) : Stream
{
    private int _position;

    public long BytesRead => _position;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = Math.Min(Math.Min(buffer.Length, maxRead), data.Length - _position);
        data.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A seekable stream whose <see cref="Length"/> claims fewer (or more) bytes than it actually returns.</summary>
public sealed class DeclaredLengthStream(byte[] data, long declaredLength) : Stream
{
    private long _position;

    public long BytesRead => _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => declaredLength;
    public override long Position { get => _position; set => _position = value; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Clamp(data.Length - _position, 0, buffer.Length);
        data.AsSpan((int)_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => declaredLength + offset,
    };

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A stream that never ends; optionally seekable with a small declared length.</summary>
public sealed class EndlessStream(bool seekable = false, long declaredLength = 0) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => seekable;
    public override bool CanWrite => false;
    public override long Length => seekable ? declaredLength : throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        buffer.Fill(0x89);
        BytesRead += buffer.Length;
        return buffer.Length;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>A write-only stream that cannot seek; the written bytes are available through <see cref="ToArray"/>.</summary>
public sealed class NonSeekableWriteStream : Stream
{
    private readonly MemoryStream _data = new();

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public byte[] ToArray() => _data.ToArray();

    public override void Write(byte[] buffer, int offset, int count) => _data.Write(buffer, offset, count);
    public override void Write(ReadOnlySpan<byte> buffer) => _data.Write(buffer);
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
