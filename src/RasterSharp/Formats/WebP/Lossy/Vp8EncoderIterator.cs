using System.Buffers;

namespace RasterSharp.Formats.WebP.Lossy;

internal sealed class Vp8EncoderIterator : IDisposable
{
    public Vp8EncoderContext Context { get; }
    public int X { get; private set; }
    public int Y { get; private set; }
    public int Index => Y * Context.MbWidth + X;
    public int PredOffset => (Y * 4 + 1) * Context.PredStride + X * 4 + 1;
    public ref Vp8Macroblock Block => ref Context.Blocks[Index];
    public byte[] Input { get; } = ArrayPool<byte>.Shared.Rent(512);
    public byte[] Output { get; set; } = ArrayPool<byte>.Shared.Rent(512);
    public byte[] Output2 { get; set; } = ArrayPool<byte>.Shared.Rent(512);
    public byte[] Predictions { get; } = ArrayPool<byte>.Shared.Rent(1792);
    public byte[] LeftY { get; } = new byte[17];
    public byte[] LeftUv { get; } = new byte[33];
    public int[] TopNz { get; } = new int[9];
    public int[] LeftNz { get; } = new int[9];
    public sbyte[] LeftError { get; } = new sbyte[4];
    private readonly byte[] _analysisTop = new byte[32], _i4Boundary = new byte[37];
    private bool _analysis;
    public int I4 { get; private set; }
    public bool DoTrellis { get; set; }
    public Span<byte> TopY => _analysis ? _analysisTop.AsSpan(0, 16) : Context.TopY.AsSpan(X * 16, 16);
    public Span<byte> TopUv => _analysis ? _analysisTop.AsSpan(16, 16) : Context.TopUv.AsSpan(X * 16, 16);
    private static ReadOnlySpan<byte> TopLeftI4 => [17, 21, 25, 29, 13, 17, 21, 25, 9, 13, 17, 21, 5, 9, 13, 17];

    public Vp8EncoderIterator(Vp8EncoderContext context) { Context = context; Reset(); }

    // port: src/enc/iterator_enc.c VP8IteratorReset, InitTop, InitLeft
    public void Reset()
    {
        X = Y = 0; DoTrellis = false; _analysis = false;
        Context.TopY.AsSpan(0, 16 * Context.MbWidth).Fill(127);
        Context.TopUv.AsSpan(0, 16 * Context.MbWidth).Fill(127);
        Context.Nonzero.AsSpan(0, Context.MbWidth + 1).Clear();
        Context.TopError.AsSpan(0, Context.MbWidth * 4).Clear();
        InitLeft();
    }
    private void InitLeft()
    {
        LeftY.AsSpan().Fill(129); LeftUv.AsSpan().Fill(129);
        LeftY[0] = LeftUv[0] = LeftUv[16] = (byte)(Y > 0 ? 129 : 127);
        LeftNz[8] = 0; LeftError.AsSpan().Clear();
    }

    // port: src/enc/iterator_enc.c VP8IteratorImport, ImportBlock, ImportLine
    public void Import(bool analysis = false)
    {
        _analysis = analysis;
        var source = Context.Source;
        ImportPlane(source.Y, source.Width, source.Width, source.Height, X * 16, Y * 16, 16, Input, 0, LeftY, _analysisTop.AsSpan(0, 16), analysis);
        ImportPlane(source.U, source.UvWidth, source.UvWidth, source.UvHeight, X * 8, Y * 8, 8, Input, 16, LeftUv, _analysisTop.AsSpan(16, 8), analysis);
        ImportPlane(source.V, source.UvWidth, source.UvWidth, source.UvHeight, X * 8, Y * 8, 8, Input, 24, LeftUv.AsSpan(16), _analysisTop.AsSpan(24, 8), analysis);
    }
    private static void ImportPlane(byte[] source, int stride, int width, int height, int x, int y, int size, byte[] output, int offset, Span<byte> left, Span<byte> top, bool analysis)
    {
        for (int j = 0; j < size; j++)
        for (int i = 0; i < size; i++)
            output[offset + j * 32 + i] = source[Math.Min(y + j, height - 1) * stride + Math.Min(x + i, width - 1)];
        if (!analysis) return;
        left[0] = x > 0 && y > 0 ? source[(y - 1) * stride + x - 1] : (byte)(y > 0 ? 129 : 127);
        for (int j = 0; j < size; j++) left[j + 1] = x == 0 ? (byte)129 : source[Math.Min(y + j, height - 1) * stride + x - 1];
        for (int i = 0; i < size; i++) top[i] = y == 0 ? (byte)127 : source[(y - 1) * stride + Math.Min(x + i, width - 1)];
    }

    // port: src/enc/quant_enc.c VP8MakeLuma16Preds, VP8MakeChroma8Preds
    public void MakePredictions()
    {
        Vp8EncoderDsp.PredLuma16(Predictions, X > 0 ? LeftY : default, Y > 0 ? TopY : default);
        Vp8EncoderDsp.PredChroma8(Predictions, X > 0 ? LeftUv : default, Y > 0 ? TopUv : default);
    }

    // port: src/enc/iterator_enc.c VP8SetIntra16Mode, VP8SetIntra4Mode
    public void SetIntra16Mode(int mode)
    {
        Block.Type = 1;
        for (int y = 0; y < 4; y++) Context.Modes.AsSpan(PredOffset + y * Context.PredStride, 4).Fill((byte)mode);
    }
    public void SetIntra4Mode(ReadOnlySpan<byte> modes)
    {
        Block.Type = 0;
        for (int y = 0; y < 4; y++) modes.Slice(y * 4, 4).CopyTo(Context.Modes.AsSpan(PredOffset + y * Context.PredStride, 4));
    }

    // port: src/enc/iterator_enc.c VP8IteratorNzToBytes, VP8IteratorBytesToNz
    public void NzToBytes()
    {
        uint tnz = Context.Nonzero[X + 1], lnz = Context.Nonzero[X];
        ReadOnlySpan<int> topBits = [12, 13, 14, 15, 18, 19, 22, 23, 24], leftBits = [3, 7, 11, 15, 17, 19, 21, 23];
        for (int i = 0; i < 9; i++) TopNz[i] = (int)((tnz >> topBits[i]) & 1);
        for (int i = 0; i < 8; i++) LeftNz[i] = (int)((lnz >> leftBits[i]) & 1);
    }
    public void BytesToNz()
    {
        ReadOnlySpan<int> topBits = [12, 13, 14, 15, 18, 19, 22, 23, 24], leftBits = [3, 7, 11, 15, 17, 19, 21, 23];
        uint nz = 0;
        for (int i = 0; i < 9; i++) nz |= (uint)TopNz[i] << topBits[i];
        for (int i = 0; i < 8; i++) if (i is 0 or 1 or 2 or 4 or 6) nz |= (uint)LeftNz[i] << leftBits[i];
        Context.Nonzero[X + 1] = nz;
    }

    // port: src/enc/iterator_enc.c VP8IteratorSaveBoundary
    public void SaveBoundary()
    {
        if (X < Context.MbWidth - 1)
        {
            for (int i = 0; i < 16; i++) LeftY[i + 1] = Output[15 + i * 32];
            for (int i = 0; i < 8; i++) { LeftUv[i + 1] = Output[23 + i * 32]; LeftUv[17 + i] = Output[31 + i * 32]; }
            LeftY[0] = TopY[15]; LeftUv[0] = TopUv[7]; LeftUv[16] = TopUv[15];
        }
        if (Y < Context.MbHeight - 1)
        {
            Output.AsSpan(15 * 32, 16).CopyTo(TopY);
            Output.AsSpan(7 * 32 + 16, 16).CopyTo(TopUv);
        }
    }

    // port: src/enc/iterator_enc.c VP8IteratorNext
    public bool Next()
    {
        if (++X == Context.MbWidth) { X = 0; Y++; InitLeft(); }
        return Y < Context.MbHeight;
    }

    // port: src/enc/iterator_enc.c VP8IteratorStartI4, VP8IteratorRotateI4
    public void StartI4()
    {
        I4 = 0;
        for (int i = 0; i < 17; i++) _i4Boundary[i] = LeftY[16 - i];
        TopY.CopyTo(_i4Boundary.AsSpan(17, 16));
        if (X < Context.MbWidth - 1) Context.TopY.AsSpan((X + 1) * 16, 4).CopyTo(_i4Boundary.AsSpan(33));
        else _i4Boundary.AsSpan(33, 4).Fill(_i4Boundary[32]);
        NzToBytes();
    }
    public void MakeI4Predictions() => Vp8EncoderDsp.PredLuma4(Predictions, _i4Boundary.AsSpan(TopLeftI4[I4] - 5, 13));
    public bool RotateI4(ReadOnlySpan<byte> output)
    {
        int block = Vp8EncoderDsp.Scan[I4], top = TopLeftI4[I4];
        for (int i = 0; i < 4; i++) _i4Boundary[top - 4 + i] = output[block + i + 3 * 32];
        if ((I4 & 3) != 3) for (int i = 0; i < 3; i++) _i4Boundary[top + i] = output[block + 3 + (2 - i) * 32];
        else for (int i = 0; i < 4; i++) _i4Boundary[top + i] = _i4Boundary[top + i + 4];
        return ++I4 < 16;
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(Input); ArrayPool<byte>.Shared.Return(Output);
        ArrayPool<byte>.Shared.Return(Output2); ArrayPool<byte>.Shared.Return(Predictions);
    }
}
