using System.Buffers;

namespace WebpSharp.Formats.WebP.Lossy;

internal sealed class Vp8TokenBuffer : IDisposable
{
    private const int PageSize = 8192;
    private readonly List<ushort[]> _pages = [];
    private int _count;

    // port: src/enc/token_enc.c AddToken, AddConstantToken, TBufferNewPage
    public void Add(bool bit, int probability, bool constant)
    {
        int page = _count / PageSize, offset = _count % PageSize;
        if (page == _pages.Count) _pages.Add(ArrayPool<ushort>.Shared.Rent(PageSize));
        _pages[page][offset] = (ushort)((bit ? 1 << 15 : 0) | (constant ? 1 << 14 : 0) | probability);
        _count++;
    }

    // port: src/enc/token_enc.c VP8EmitTokens, VP8EstimateTokenSize
    public long Emit(Vp8BitWriter? writer, ReadOnlySpan<byte> probabilities)
    {
        long size = 0;
        for (int i = 0; i < _count; i++)
        {
            ushort token = _pages[i / PageSize][i % PageSize];
            bool bit = (token & 0x8000) != 0;
            int probability = (token & 0x4000) != 0 ? token & 255 : probabilities[token & 0x3fff];
            if (writer is not null) writer.Put(bit, probability);
            else size += Vp8EncoderProba.BitCost(bit, probability);
        }
        return size;
    }
    public void Clear() => _count = 0;
    public void Dispose() { foreach (ushort[] page in _pages) ArrayPool<ushort>.Shared.Return(page); _pages.Clear(); }
}
