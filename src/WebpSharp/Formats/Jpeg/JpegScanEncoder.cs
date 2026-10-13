namespace WebpSharp.Formats.Jpeg;

internal readonly record struct JpegEncoderScan(int Component, int Start, int End, int High, int Low)
{
    public bool Interleaved => Component < 0;
}

internal sealed class JpegScanEncoder
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
    public void Encode()
    {
        JpegEncoderComponent first = _components[_scan.Interleaved ? 0 : _scan.Component];
        int width = _scan.Interleaved ? first.PaddedWidth / first.Horizontal : first.WidthInBlocks;
        int height = _scan.Interleaved ? first.PaddedHeight / first.Vertical : first.HeightInBlocks;
        int mcu = 0, restart = 0;
        for (int y = 0; y < height; y++)
        {
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

    // port: libjpeg-turbo jchuff.c encode_one_block; jcphuff.c encode_mcu_DC_refine
    private void EncodeBlock(ReadOnlySpan<short> block, int component)
    {
        int table = component == 0 ? 0 : 2;
        if (!_progressive)
        {
            EncodeDc(block[0], component, table);
            int run = 0;
            for (int k = 1; k < 64; k++)
            {
                int value = block[JpegTables.NaturalOrder[k]];
                if (value == 0) { run++; continue; }
                while (run >= 16) { Symbol(table + 1, 0xF0); run -= 16; }
                int size = JpegHuffmanEncoder.MagnitudeBits(value);
                Symbol(table + 1, (run << 4) | size);
                Bits(value < 0 ? value - 1 : value, size);
                run = 0;
            }

            if (run != 0) Symbol(table + 1, 0);
        }
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
        Symbol(table, size);
        Bits(delta < 0 ? delta - 1 : delta, size);
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
        int run = 0;
        for (int k = _scan.Start; k <= _scan.End; k++)
        {
            int original = block[JpegTables.NaturalOrder[k]];
            int value = Math.Abs(original) >> _scan.Low;
            if (value == 0) { run++; continue; }
            EmitEobRun();
            while (run >= 16) { Symbol(AcTable, 0xF0); run -= 16; }
            int size = JpegHuffmanEncoder.MagnitudeBits(value);
            Symbol(AcTable, (run << 4) | size);
            Bits(original < 0 ? ~value : value, size);
            run = 0;
        }

        if (run > 0 && ++_eobRun == 0x7FFF) EmitEobRun();
    }

    // port: libjpeg-turbo jcphuff.c encode_mcu_AC_refine
    private void EncodeAcRefine(ReadOnlySpan<short> block)
    {
        Span<byte> corrections = stackalloc byte[64];
        int count = 0, run = 0, lastNew = 0;
        for (int k = _scan.Start; k <= _scan.End; k++)
            if ((Math.Abs(block[JpegTables.NaturalOrder[k]]) >> _scan.Low) == 1) lastNew = k;

        for (int k = _scan.Start; k <= _scan.End; k++)
        {
            int original = block[JpegTables.NaturalOrder[k]];
            int value = Math.Abs(original) >> _scan.Low;
            if (value == 0) { run++; continue; }
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

        if (run > 0 || count > 0)
        {
            _eobRun++;
            corrections[..count].CopyTo(_corrections.AsSpan(_correctionCount));
            _correctionCount += count;
            if (_eobRun == 0x7FFF || _correctionCount > 937) EmitEobRun();
        }
    }
}
