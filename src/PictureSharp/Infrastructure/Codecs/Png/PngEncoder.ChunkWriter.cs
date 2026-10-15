using System.Buffers.Binary;
using System.Text;
using PictureSharp.Algorithms.Compression;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngEncoder
{
    private sealed class ChunkWriter(Stream output)
    {
        public void Write(string type, ReadOnlySpan<byte> data)
        {
            Span<byte> header = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
            Encoding.ASCII.GetBytes(type, header[4..]);
            output.Write(header);
            output.Write(data);
            uint crc = Crc32.Update(Crc32.Compute(header[4..]), data);
            Span<byte> trailer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
            output.Write(trailer);
        }
    }
}
