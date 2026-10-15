using System.Numerics;
using System.Runtime.CompilerServices;

namespace PictureSharp.Formats.Jpeg;

internal readonly record struct JpegEncoderScan(int Component, int Start, int End, int High, int Low)
{
    public bool Interleaved => Component < 0;
}

internal sealed partial class JpegScanEncoder
{
    private readonly JpegEncoderComponent[] _components;
    private readonly JpegEncoderScan _scan;
    private readonly bool _progressive;
    private readonly int _restartInterval;
    private readonly JpegBitWriter? _writer;
    private readonly JpegHuffmanEncoder?[] _tables;
    private readonly int[] _lastDc = new int[3];
    private readonly byte[] _corrections = new byte[1000];
    private int _correctionCount;
    private int _eobRun;
    private int AcTable => (_scan.Component == 0 ? 0 : 2) + 1;
    public long[][] Frequencies { get; } = [new long[257], new long[257], new long[257], new long[257]];

    public JpegScanEncoder(JpegEncoderComponent[] components, JpegEncoderScan scan, bool progressive,
        int restartInterval, JpegHuffmanEncoder?[] tables, Stream? output)
    {
        _components = components;
        _scan = scan;
        _progressive = progressive;
        _restartInterval = restartInterval;
        _tables = tables;
        _writer = output is null ? null : new JpegBitWriter(output);
    }

    // port: libjpeg-turbo jccoefct.c compress_output; jcphuff.c emit_restart
    public void Encode(CancellationToken cancellationToken = default)
    {
        JpegEncoderComponent first = _components[_scan.Interleaved ? 0 : _scan.Component];
        int width = _scan.Interleaved ? first.PaddedWidth / first.Horizontal : first.WidthInBlocks;
        int height = _scan.Interleaved ? first.PaddedHeight / first.Vertical : first.HeightInBlocks;
        int mcu = 0, restart = 0;
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++, mcu++)
            {
                if (_restartInterval != 0 && mcu != 0 && mcu % _restartInterval == 0)
                {
                    EmitEobRun();
                    _writer?.Restart(restart++);
                    Array.Clear(_lastDc);
                }

                if (_scan.Interleaved)
                {
                    for (int c = 0; c < _components.Length; c++)
                    {
                        JpegEncoderComponent component = _components[c];
                        for (int by = 0; by < component.Vertical; by++)
                            for (int bx = 0; bx < component.Horizontal; bx++)
                                EncodeBlock(component.Block(x * component.Horizontal + bx, y * component.Vertical + by), c);
                    }
                }
                else EncodeBlock(first.Block(x, y), _scan.Component);
            }
        }

        EmitEobRun();
        _writer?.Flush();
    }

    private void Symbol(int table, int symbol)
    {
        if (_writer is null) Frequencies[table][symbol]++;
        else
        {
            JpegHuffmanEncoder huffman = _tables[table]!;
            if (huffman.Sizes[symbol] == 0) throw new InvalidOperationException("Missing JPEG Huffman symbol.");
            _writer.Write(huffman.Codes[symbol], huffman.Sizes[symbol]);
        }
    }

    private void Bits(int value, int count) => _writer?.Write(value, count);

    // A Huffman symbol followed by its 'count' extra bits, in one write.
    private void SymbolBits(int table, int symbol, int value, int count)
    {
        if (_writer is null) Frequencies[table][symbol]++;
        else
        {
            JpegHuffmanEncoder huffman = _tables[table]!;
            int size = huffman.Sizes[symbol];
            if (size == 0) throw new InvalidOperationException("Missing JPEG Huffman symbol.");
            _writer.Write((huffman.Codes[symbol] << count) | (value & ((1 << count) - 1)), size + count);
        }
    }

    // port: libjpeg-turbo jchuff.c encode_one_block (jchuff-sse2.asm finds the non-zero coefficients with a bit mask)
    private void EncodeBaseline(ReadOnlySpan<short> block, int component, int table)
    {
        EncodeDc(block[0], component, table);
        ReadOnlySpan<short> zigzag = block;
        ulong mask = JpegScanEncoder.NonZeroMask(zigzag) & ~1UL;
        if (_writer is null)
        {
            CountBaseline(zigzag, mask, Frequencies[table + 1]);
            return;
        }

        JpegHuffmanEncoder huffman = _tables[table + 1]!;
        ushort[] codes = huffman.Codes;
        byte[] sizes = huffman.Sizes;
        JpegBitWriter writer = _writer;
        int last = 0;
        while (mask != 0)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int run = k - last - 1;
            last = k;
            for (; run >= 16; run -= 16) writer.Write(codes[0xF0], Size(sizes, 0xF0));
            int value = zigzag[k];
            int size = JpegHuffmanEncoder.MagnitudeBits(value);
            int symbol = (run << 4) | size;
            writer.Write((codes[symbol] << size) | ((value < 0 ? value - 1 : value) & ((1 << size) - 1)), Size(sizes, symbol) + size);
        }

        if (last != 63) writer.Write(codes[0], Size(sizes, 0));
    }

    private static void CountBaseline(ReadOnlySpan<short> zigzag, ulong mask, long[] frequencies)
    {
        int last = 0;
        while (mask != 0)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int run = k - last - 1;
            last = k;
            for (; run >= 16; run -= 16) frequencies[0xF0]++;
            frequencies[(run << 4) | JpegHuffmanEncoder.MagnitudeBits(zigzag[k])]++;
        }

        if (last != 63) frequencies[0]++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Size(byte[] sizes, int symbol)
    {
        int size = sizes[symbol];
        return size != 0 ? size : throw new InvalidOperationException("Missing JPEG Huffman symbol.");
    }

    // port: libjpeg-turbo jchuff.c encode_one_block; jcphuff.c encode_mcu_DC_refine. Blocks are in zigzag order.
    private void EncodeBlock(ReadOnlySpan<short> block, int component)
    {
        int table = component == 0 ? 0 : 2;
        if (!_progressive) EncodeBaseline(block, component, table);
        else if (_scan.Start == 0)
        {
            if (_scan.High == 0) EncodeDc(block[0] >> _scan.Low, component, table);
            else Bits(block[0] >> _scan.Low, 1);
        }
        else if (_scan.High == 0) EncodeAcFirst(block);
        else EncodeAcRefine(block);
    }

    // port: libjpeg-turbo jchuff.c encode_one_block; jcphuff.c encode_mcu_DC_first
    private void EncodeDc(int value, int component, int table)
    {
        int delta = value - _lastDc[component];
        _lastDc[component] = value;
        int size = JpegHuffmanEncoder.MagnitudeBits(delta);
        SymbolBits(table, size, delta < 0 ? delta - 1 : delta, size);
    }

    // port: libjpeg-turbo jcphuff.c emit_eobrun
    private void EmitEobRun()
    {
        if (_eobRun == 0) return;
        int size = JpegHuffmanEncoder.MagnitudeBits(_eobRun) - 1;
        Symbol(AcTable, size << 4);
        Bits(_eobRun, size);
        for (int i = 0; i < _correctionCount; i++) Bits(_corrections[i], 1);
        _eobRun = 0;
        _correctionCount = 0;
    }

    // port: libjpeg-turbo jcphuff.c encode_mcu_AC_first
    private void EncodeAcFirst(ReadOnlySpan<short> block)
    {
        // Coefficients that stay non-zero after the point transform, within the band.
        ulong band = (_scan.End == 63 ? ulong.MaxValue : (2UL << _scan.End) - 1) & ~((1UL << _scan.Start) - 1);
        ulong mask = JpegScanEncoder.AtLeastMask(block, 1 << _scan.Low) & band;
        int last = _scan.Start - 1;
        while (mask != 0)
        {
            int k = BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;
            int run = k - last - 1;
            last = k;
            int original = block[k];
            int value = Math.Abs(original) >> _scan.Low;
            EmitEobRun();
            while (run >= 16) { Symbol(AcTable, 0xF0); run -= 16; }
            int size = JpegHuffmanEncoder.MagnitudeBits(value);
            SymbolBits(AcTable, (run << 4) | size, original < 0 ? ~value : value, size);
        }

        if (last < _scan.End && ++_eobRun == 0x7FFF) EmitEobRun();
    }

    // port: libjpeg-turbo jcphuff.c encode_mcu_AC_refine
    private void EncodeAcRefine(ReadOnlySpan<short> block)
    {
        // Only non-zero coefficients matter: zeros extend the run, coefficients that were already non-zero add a
        // correction bit, and newly non-zero ones (value 1) emit a symbol.
        Span<byte> corrections = stackalloc byte[64];
        ulong band = (_scan.End == 63 ? ulong.MaxValue : (2UL << _scan.End) - 1) & ~((1UL << _scan.Start) - 1);
        ulong nonZero = JpegScanEncoder.AtLeastMask(block, 1 << _scan.Low) & band;
        ulong newlyNonZero = nonZero & ~JpegScanEncoder.AtLeastMask(block, 2 << _scan.Low);
        int lastNew = newlyNonZero == 0 ? 0 : 63 - BitOperations.LeadingZeroCount(newlyNonZero);
        int count = 0, run = 0, previous = _scan.Start - 1;
        while (nonZero != 0)
        {
            int k = BitOperations.TrailingZeroCount(nonZero);
            nonZero &= nonZero - 1;
            run += k - previous - 1;
            previous = k;
            int original = block[k];
            int value = Math.Abs(original) >> _scan.Low;
            while (run > 15 && k <= lastNew)
            {
                EmitEobRun();
                Symbol(AcTable, 0xF0);
                run -= 16;
                for (int i = 0; i < count; i++) Bits(corrections[i], 1);
                count = 0;
            }

            if (value > 1) { corrections[count++] = (byte)(value & 1); continue; }
            EmitEobRun();
            Symbol(AcTable, (run << 4) | 1);
            Bits(original < 0 ? 0 : 1, 1);
            for (int i = 0; i < count; i++) Bits(corrections[i], 1);
            count = 0;
            run = 0;
        }

        run += _scan.End - previous;
        if (run > 0 || count > 0)
        {
            _eobRun++;
            corrections[..count].CopyTo(_corrections.AsSpan(_correctionCount));
            _correctionCount += count;
            if (_eobRun == 0x7FFF || _correctionCount > 937) EmitEobRun();
        }
    }
}
