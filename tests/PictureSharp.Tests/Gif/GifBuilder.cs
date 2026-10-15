using System.Buffers.Binary;
using PictureSharp.Formats.Gif;

namespace PictureSharp.Tests.Gif;

/// <summary>Writes GIF block by block for tests that need layouts the encoder never produces.</summary>
internal sealed class GifBuilder
{
    private readonly MemoryStream _stream = new();

    public GifBuilder(int width, int height, Rgba32[]? globalTable = null, int background = 0)
    {
        _stream.Write("GIF89a"u8);
        Span<byte> screen = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(screen[2..], (ushort)height);
        screen[4] = globalTable is null ? (byte)0 : (byte)(0xF0 | (Bits(globalTable.Length) - 1));
        screen[5] = (byte)background;
        _stream.Write(screen);
        if (globalTable is not null)
        {
            WriteTable(globalTable);
        }
    }

    public static Rgba32[] Palette4 { get; } = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255), new(255, 255, 0)];

    public GifBuilder Control(int delay = 0, int transparent = -1, int disposal = 0)
    {
        _stream.Write([0x21, 0xF9, 4, (byte)((disposal << 2) | (transparent >= 0 ? 1 : 0)), (byte)delay, (byte)(delay >> 8),
            (byte)Math.Max(transparent, 0), 0]);
        return this;
    }

    public GifBuilder Loop(int repetitions)
    {
        _stream.Write([0x21, 0xFF, 11]);
        _stream.Write("NETSCAPE2.0"u8);
        _stream.Write([3, 1, (byte)repetitions, (byte)(repetitions >> 8), 0]);
        return this;
    }

    public GifBuilder Image(int x, int y, int width, int height, byte[] indices, Rgba32[]? localTable = null, int minCodeSize = 2, byte[]? lzw = null)
    {
        Span<byte> descriptor = stackalloc byte[10];
        descriptor[0] = 0x2C;
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[1..], (ushort)x);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[3..], (ushort)y);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[5..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[7..], (ushort)height);
        descriptor[9] = localTable is null ? (byte)0 : (byte)(0x80 | (Bits(localTable.Length) - 1));
        _stream.Write(descriptor);
        if (localTable is not null)
        {
            WriteTable(localTable);
        }

        if (lzw is null)
        {
            GifLzwEncoder.Encode(indices, minCodeSize, _stream);
        }
        else
        {
            _stream.WriteByte((byte)minCodeSize);
            _stream.Write(lzw);
        }

        return this;
    }

    public GifBuilder Raw(params byte[] bytes)
    {
        _stream.Write(bytes);
        return this;
    }

    public byte[] ToArray(bool trailer = true)
    {
        if (trailer)
        {
            _stream.WriteByte(0x3B);
        }

        return _stream.ToArray();
    }

    private void WriteTable(Rgba32[] table)
    {
        int count = 1 << Bits(table.Length);
        for (int i = 0; i < count; i++)
        {
            Rgba32 c = i < table.Length ? table[i] : default;
            _stream.Write([c.R, c.G, c.B]);
        }
    }

    private static int Bits(int count)
    {
        int bits = 1;
        while ((1 << bits) < count)
        {
            bits++;
        }

        return bits;
    }
}
