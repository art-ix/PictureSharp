using System.Buffers;
using System.Numerics;

namespace WebpSharp.Formats.WebP.Lossless;

internal sealed class Vp8LHuffmanEncoder
{
    public byte[] Lengths { get; }
    public ushort[] Codes { get; }
    private readonly record struct Node(uint Count, int Value, int Left, int Right);
    private static readonly IComparer<Node> NodeComparer = Comparer<Node>.Create(static (a, b) => a.Count != b.Count ? b.Count.CompareTo(a.Count) : a.Value.CompareTo(b.Value));
    private readonly record struct Token(int Code, int Extra);
    private static ReadOnlySpan<byte> StorageOrder => [17, 18, 0, 1, 2, 3, 4, 5, 16, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    // port: src/utils/huffman_encode_utils.c VP8LCreateHuffmanTree
    public Vp8LHuffmanEncoder(ReadOnlySpan<uint> histogram, int limit = 15)
    {
        Lengths = new byte[histogram.Length];
        Codes = new ushort[histogram.Length];
        uint[] counts = ArrayPool<uint>.Shared.Rent(histogram.Length);
        try
        {
            histogram.CopyTo(counts);
            OptimizeHuffmanForRle(counts.AsSpan(0, histogram.Length));
            GenerateOptimalTree(counts.AsSpan(0, histogram.Length), limit);
            ConvertBitDepthsToSymbols();
        }
        finally { ArrayPool<uint>.Shared.Return(counts); }
    }

    // port: src/utils/huffman_encode_utils.c OptimizeHuffmanForRle
    private static void OptimizeHuffmanForRle(Span<uint> counts)
    {
        int length = counts.Length;
        while (length > 0 && counts[length - 1] == 0) length--;
        if (length == 0) return;
        Span<byte> good = stackalloc byte[length];
        good.Clear();
        uint symbol = counts[0];
        int stride = 0;
        for (int i = 0; i <= length; i++)
        {
            if (i == length || counts[i] != symbol)
            {
                if ((symbol == 0 && stride >= 5) || (symbol != 0 && stride >= 7)) good.Slice(i - stride, stride).Fill(1);
                stride = 1;
                if (i != length) symbol = counts[i];
            }
            else stride++;
        }
        stride = 0;
        uint limit = counts[0], sum = 0;
        for (int i = 0; i <= length; i++)
        {
            if (i == length || good[i] != 0 || (i != 0 && good[i - 1] != 0) || Math.Abs((long)counts[i] - limit) >= 4)
            {
                if (stride >= 4 || (stride >= 3 && sum == 0))
                {
                    uint count = sum == 0 ? 0 : Math.Max(1, (sum + (uint)stride / 2) / (uint)stride);
                    counts.Slice(i - stride, stride).Fill(count);
                }
                stride = 0;
                sum = 0;
                limit = i < length - 3 ? (counts[i] + counts[i + 1] + counts[i + 2] + counts[i + 3] + 2) / 4 : i < length ? counts[i] : 0;
            }
            stride++;
            if (i != length)
            {
                sum += counts[i];
                if (stride >= 4) limit = (sum + (uint)stride / 2) / (uint)stride;
            }
        }
    }

    // port: src/utils/huffman_encode_utils.c GenerateOptimalTree, SetBitDepths
    private void GenerateOptimalTree(ReadOnlySpan<uint> histogram, int limit)
    {
        Node[] nodes = ArrayPool<Node>.Shared.Rent(histogram.Length * 3);
        try
        {
            for (uint minimum = 1; ; minimum *= 2)
            {
                int size = 0;
                for (int i = 0; i < histogram.Length; i++)
                    if (histogram[i] != 0) nodes[size++] = new Node(Math.Max(minimum, histogram[i]), i, -1, -1);
                if (size == 0) return;
                Array.Sort(nodes, 0, size, NodeComparer);
                int pool = histogram.Length, used = pool;
                if (size == 1) Lengths[nodes[0].Value] = 1;
                else
                {
                    while (size > 1)
                    {
                        nodes[used++] = nodes[--size];
                        nodes[used++] = nodes[--size];
                        uint count = nodes[used - 1].Count + nodes[used - 2].Count;
                        int k = 0;
                        while (k < size && nodes[k].Count > count) k++;
                        Array.Copy(nodes, k, nodes, k + 1, size - k);
                        nodes[k] = new Node(count, -1, used - 1, used - 2);
                        size++;
                    }
                    SetDepth(nodes, nodes[0], 0);
                }
                if (Lengths.Max() <= limit) return;
            }
        }
        finally { ArrayPool<Node>.Shared.Return(nodes); }
    }

    private void SetDepth(Node[] pool, Node node, int depth)
    {
        if (node.Left < 0) Lengths[node.Value] = (byte)depth;
        else { SetDepth(pool, pool[node.Left], depth + 1); SetDepth(pool, pool[node.Right], depth + 1); }
    }

    // port: src/utils/huffman_encode_utils.c ConvertBitDepthsToSymbols, ReverseBits
    private void ConvertBitDepthsToSymbols()
    {
        Span<int> counts = stackalloc int[16];
        Span<int> next = stackalloc int[16];
        counts.Clear(); next.Clear();
        foreach (byte length in Lengths) if (length != 0) counts[length]++;
        for (int i = 1; i < 16; i++) next[i] = (next[i - 1] + counts[i - 1]) << 1;
        for (int i = 0; i < Lengths.Length; i++)
        {
            int length = Lengths[i], code = next[length]++, reverse = 0;
            for (int j = 0; j < length; j++) { reverse = (reverse << 1) | (code & 1); code >>= 1; }
            Codes[i] = (ushort)reverse;
        }
    }

    public void Write(Vp8LBitWriter writer, int symbol) => writer.Put(Codes[symbol], Lengths[symbol]);

    // port: src/enc/vp8l_enc.c ClearHuffmanTreeIfOnlyOneSymbol
    private void ClearTrivial()
    {
        if (Lengths.Count(l => l != 0) <= 1) { Array.Clear(Lengths); Array.Clear(Codes); }
    }

    // port: src/enc/vp8l_enc.c StoreHuffmanCode
    public void Store(Vp8LBitWriter writer)
    {
        Span<int> symbols = stackalloc int[3];
        int count = 0;
        for (int i = 0; i < Lengths.Length && count < 3; i++) if (Lengths[i] != 0) symbols[count++] = i;
        if (count == 0) writer.Put(1, 4);
        else if (count <= 2 && symbols[0] < 256 && (count == 1 || symbols[1] < 256))
        {
            writer.Put(1, 1); writer.Put(count - 1, 1);
            writer.Put(symbols[0] > 1 ? 1 : 0, 1);
            writer.Put(symbols[0], symbols[0] > 1 ? 8 : 1);
            if (count == 2) writer.Put(symbols[1], 8);
        }
        else StoreFull(writer);
        ClearTrivial();
    }

    // port: src/utils/huffman_encode_utils.c VP8LCreateCompressedHuffmanTree, CodeRepeatedValues, CodeRepeatedZeros
    private List<Token> CompressLengths()
    {
        var tokens = new List<Token>();
        int previous = 8;
        for (int i = 0; i < Lengths.Length;)
        {
            int value = Lengths[i], end = i + 1;
            while (end < Lengths.Length && Lengths[end] == value) end++;
            int count = end - i;
            i = end;
            if (value != 0 && value != previous) { tokens.Add(new Token(value, 0)); count--; }
            while (count > 0)
            {
                if (count < 3) { tokens.Add(new Token(value, 0)); count--; }
                else if (value != 0) { int run = Math.Min(count, 6); tokens.Add(new Token(16, run - 3)); count -= run; }
                else if (count < 11) { tokens.Add(new Token(17, count - 3)); count = 0; }
                else { int run = Math.Min(count, 138); tokens.Add(new Token(18, run - 11)); count -= run; }
            }
            if (value != 0) previous = value;
        }
        return tokens;
    }

    // port: src/enc/vp8l_enc.c StoreFullHuffmanCode, StoreHuffmanTreeOfHuffmanTreeToBitMask
    private void StoreFull(Vp8LBitWriter writer)
    {
        writer.Put(0, 1);
        List<Token> tokens = CompressLengths();
        Span<uint> counts = stackalloc uint[19]; counts.Clear();
        foreach (Token token in tokens) counts[token.Code]++;
        var code = new Vp8LHuffmanEncoder(counts, 7);
        int store = 19;
        while (store > 4 && code.Lengths[StorageOrder[store - 1]] == 0) store--;
        writer.Put(store - 4, 4);
        for (int i = 0; i < store; i++) writer.Put(code.Lengths[StorageOrder[i]], 3);
        code.ClearTrivial();
        int trim = tokens.Count, saved = 0;
        while (trim > 0 && tokens[trim - 1].Code is 0 or 17 or 18)
        {
            int symbol = tokens[--trim].Code;
            saved += code.Lengths[symbol] + (symbol == 17 ? 3 : symbol == 18 ? 7 : 0);
        }
        bool trimmed = trim > 1 && saved > 12;
        writer.Put(trimmed ? 1 : 0, 1);
        if (trimmed)
        {
            int pairs = trim == 2 ? 1 : BitOperations.Log2((uint)(trim - 2)) / 2 + 1;
            writer.Put(pairs - 1, 3); writer.Put(trim - 2, pairs * 2);
        }
        else trim = tokens.Count;
        for (int i = 0; i < trim; i++)
        {
            Token token = tokens[i]; code.Write(writer, token.Code);
            writer.Put(token.Extra, token.Code == 16 ? 2 : token.Code == 17 ? 3 : token.Code == 18 ? 7 : 0);
        }
    }
}
