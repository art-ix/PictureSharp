using System.Buffers;
using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.WebP.Lossy;

internal sealed class Vp8TokenBuffer : IDisposable
{
    private const int PageSize = 8192;
    private readonly List<ushort[]> _pages = [];
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
        for (int page = 0, remaining = _count; remaining > 0; page++, remaining -= PageSize)
        {
            ReadOnlySpan<ushort> tokens = _pages[page].AsSpan(0, Math.Min(remaining, PageSize));
            if (writer is null)
            {
                foreach (ushort token in tokens) size += Vp8EncoderProba.BitCost((token & 0x8000) != 0, Probability(token, probabilities));
            }
            else
            {
                foreach (ushort token in tokens) writer.Put((token & 0x8000) != 0, Probability(token, probabilities));
            }
        }
        return size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Probability(ushort token, ReadOnlySpan<byte> probabilities) =>
        (token & 0x4000) != 0 ? token & 255 : probabilities[token & 0x3fff];

    public void AddRaw(ushort token)
    {
        int page = _count / PageSize, offset = _count % PageSize;
        if (page == _pages.Count) _pages.Add(ArrayPool<ushort>.Shared.Rent(PageSize));
        _pages[page][offset] = token;
        _count++;
    }

    public void Clear() => _count = 0;
    public void Dispose() { foreach (ushort[] page in _pages) ArrayPool<ushort>.Shared.Return(page); _pages.Clear(); }
}
