using System.Buffers;
using System.Runtime.CompilerServices;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

// libwebp chains fixed-size pages; one array that doubles keeps a large image's tokens in a single pooled
// buffer (thousands of small pages would mostly miss the shared pool and be allocated on every encode).
internal sealed class Vp8TokenBuffer : IDisposable
{
    private ushort[] _tokens = ArrayPool<ushort>.Shared.Rent(8192);
    private int _count;

    // port: src/enc/token_enc.c AddToken, AddConstantToken, TBufferNewPage
    public void Add(bool bit, int probability, bool constant)
    {
        AddRaw(Token(bit, probability, constant));
    }

    public static ushort Token(bool bit, int probability, bool constant) =>
        (ushort)((bit ? 1 << 15 : 0) | (constant ? 1 << 14 : 0) | probability);

    // port: src/enc/token_enc.c VP8EmitTokens, VP8EstimateTokenSize
    public long Emit(Vp8BitWriter? writer, ReadOnlySpan<byte> probabilities)
    {
        long size = 0;
        ReadOnlySpan<ushort> tokens = _tokens.AsSpan(0, _count);
        if (writer is null)
        {
            foreach (ushort token in tokens) size += Vp8EncoderProba.BitCost((token & 0x8000) != 0, Probability(token, probabilities));
        }
        else
        {
            writer.PutTokens(tokens, probabilities);
        }
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Probability(ushort token, ReadOnlySpan<byte> probabilities) =>
        (token & 0x4000) != 0 ? token & 255 : probabilities[token & 0x3fff];

    public void AddRaw(ushort token)
    {
        if (_count == _tokens.Length) Grow();
        _tokens[_count++] = token;
    }

    private void Grow()
    {
        ushort[] next = ArrayPool<ushort>.Shared.Rent(_tokens.Length * 2);
        _tokens.AsSpan(0, _count).CopyTo(next);
        ArrayPool<ushort>.Shared.Return(_tokens);
        _tokens = next;
    }

    public void Clear() => _count = 0;
    public void Dispose() => ArrayPool<ushort>.Shared.Return(_tokens);
}
