using RasterSharp.Internal;
using static RasterSharp.Formats.WebP.Lossy.Vp8Dsp;

namespace RasterSharp.Formats.WebP.Lossy;

/// <summary>Decoded VP8 frame: YUV 4:2:0 planes padded to whole macroblocks.</summary>
internal sealed class Vp8Frame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Y { get; init; }
    public required byte[] U { get; init; }
    public required byte[] V { get; init; }
    public required int YStride { get; init; }
    public required int UvStride { get; init; }
}

/// <summary>Decoder for VP8 key frames (the WebP lossy bitstream).</summary>
/// <remarks>
/// libwebp reconstructs, filters and emits one macroblock row at a time through a small cache.
/// This port reconstructs the whole frame into full-size planes and then runs the in-loop filter
/// over all macroblocks in raster order. Prediction only ever reads unfiltered samples, so the
/// result is identical.
/// </remarks>
// port: src/dec/vp8_dec.c, tree_dec.c, quant_dec.c, frame_dec.c
internal sealed class Vp8Decoder
{
    // Intra prediction modes (common_dec.h)
    private const int BDcPred = 0;
    private const int BTmPred = 1;
    private const int BVePred = 2;
    private const int BHePred = 3;
    private const int BRdPred = 4;
    private const int BVrPred = 5;
    private const int BLdPred = 6;
    private const int BVlPred = 7;
    private const int BHdPred = 8;
    private const int BHuPred = 9;
    private const int DcPred = BDcPred;
    private const int VPred = BVePred;
    private const int HPred = BHePred;
    private const int TmPred = BTmPred;
    private const int BDcPredNoTop = 4;
    private const int BDcPredNoLeft = 5;
    private const int BDcPredNoTopLeft = 6;

    private const int NumMbSegments = 4;
    private const int NumRefLfDeltas = 4;
    private const int NumModeLfDeltas = 4;
    private const int MaxNumPartitions = 8;
    private const int NumTypes = 4;
    private const int NumBands = 8;
    private const int NumCtx = 3;
    private const int NumProbas = 11;
    private const int MbFeatureTreeProbs = 3;

    // Work buffer layout (vp8i_dec.h)
    private const int YuvSize = (Bps * 17) + (Bps * 9);
    private const int YOff = (Bps * 1) + 8;
    private const int UOff = YOff + (Bps * 16) + Bps;
    private const int VOff = UOff + 16;

    private static ReadOnlySpan<ushort> Scan =>
    [
        0 + (0 * Bps), 4 + (0 * Bps), 8 + (0 * Bps), 12 + (0 * Bps),
        0 + (4 * Bps), 4 + (4 * Bps), 8 + (4 * Bps), 12 + (4 * Bps),
        0 + (8 * Bps), 4 + (8 * Bps), 8 + (8 * Bps), 12 + (8 * Bps),
        0 + (12 * Bps), 4 + (12 * Bps), 8 + (12 * Bps), 12 + (12 * Bps),
    ];

    private readonly byte[] _data;
    private Vp8BitReader _br = null!;

    // Picture header
    private int _width;
    private int _height;
    private int _mbW;
    private int _mbH;

    // Segment header
    private bool _useSegment;
    private bool _updateMap;
    private bool _absoluteDelta = true;
    private readonly sbyte[] _quantizer = new sbyte[NumMbSegments];
    private readonly sbyte[] _filterStrength = new sbyte[NumMbSegments];

    // Filter header
    private bool _simple;
    private int _level;
    private int _sharpness;
    private bool _useLfDelta;
    private readonly int[] _refLfDelta = new int[NumRefLfDeltas];
    private readonly int[] _modeLfDelta = new int[NumModeLfDeltas];
    private int _filterType;  // 0=off, 1=simple, 2=complex

    // Partitions
    private int _numPartsMinusOne;
    private readonly Vp8BitReader[] _parts = new Vp8BitReader[MaxNumPartitions];

    // Dequantization
    private readonly QuantMatrix[] _dqm = new QuantMatrix[NumMbSegments];

    // Probabilities
    private readonly byte[] _segmentProbas = new byte[MbFeatureTreeProbs];
    private readonly byte[] _bands = new byte[NumTypes * NumBands * NumCtx * NumProbas];
    private bool _useSkipProba;
    private int _skipP;

    // Per-row state
    private byte[] _intraT = [];
    private readonly byte[] _intraL = new byte[4];
    private MacroblockData[] _mbData = [];
    private NzInfo[] _mbInfo = [];          // [0] is the left context, [1 + x] is column x
    private FilterInfo[] _fInfo = [];   // per macroblock of the whole frame
    private readonly FilterInfo[,] _fStrengths = new FilterInfo[NumMbSegments, 2];

    // Reconstruction
    private readonly byte[] _yuvB = new byte[YuvSize];
    private byte[] _topY = [];
    private byte[] _topU = [];
    private byte[] _topV = [];
    private byte[] _planeY = [];
    private byte[] _planeU = [];
    private byte[] _planeV = [];
    private int _yStride;
    private int _uvStride;

    private Vp8Decoder(byte[] data)
    {
        _data = data;
    }

    private static ImageFormatException Error(string message) => new(ImageFormat.WebP, message);

    /// <summary>Decodes a VP8 bitstream (the payload of a "VP8 " chunk) into YUV planes.</summary>
    public static Vp8Frame Decode(byte[] data, int offset, int length, bool bypassFiltering = false, int maxDegreeOfParallelism = 1)
    {
        var dec = new Vp8Decoder(data);
        dec.GetHeaders(offset, length);
        if (bypassFiltering)
        {
            dec._filterType = 0;
        }

        dec.PrecomputeFilterStrengths();
        dec.AllocateMemory();
        dec.ParseAndFilterFrame(maxDegreeOfParallelism);
        return new Vp8Frame
        {
            Width = dec._width,
            Height = dec._height,
            Y = dec._planeY,
            U = dec._planeU,
            V = dec._planeV,
            YStride = dec._yStride,
            UvStride = dec._uvStride,
        };
    }

    // ------------------------------------------------------------------ headers

    // port: VP8GetHeaders
    private void GetHeaders(int offset, int size)
    {
        if (size < 4)
        {
            throw Error("Truncated VP8 header.");
        }

        byte[] buf = _data;
        uint bits = (uint)(buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16));
        bool keyFrame = (bits & 1) == 0;
        int profile = (int)((bits >> 1) & 7);
        bool show = ((bits >> 4) & 1) != 0;
        uint partitionLength = bits >> 5;
        if (profile > 3)
        {
            throw Error("Incorrect keyframe parameters.");
        }

        if (!show)
        {
            throw new NotSupportedException("VP8 frame is not displayable.");
        }

        if (!keyFrame)
        {
            throw new NotSupportedException("VP8 inter frames are not supported.");
        }

        offset += 3;
        size -= 3;

        // Paragraph 9.2
        if (size < 7)
        {
            throw Error("Cannot parse VP8 picture header.");
        }

        if (buf[offset] != 0x9d || buf[offset + 1] != 0x01 || buf[offset + 2] != 0x2a)
        {
            throw Error("Bad VP8 code word.");
        }

        _width = ((buf[offset + 4] << 8) | buf[offset + 3]) & 0x3fff;
        _height = ((buf[offset + 6] << 8) | buf[offset + 5]) & 0x3fff;
        offset += 7;
        size -= 7;
        if (_width == 0 || _height == 0)
        {
            throw Error("Invalid VP8 dimensions.");
        }

        _mbW = (_width + 15) >> 4;
        _mbH = (_height + 15) >> 4;

        // VP8ResetProba / ResetSegmentHeader
        _segmentProbas.AsSpan().Fill(255);
        _useSegment = false;
        _updateMap = false;
        _absoluteDelta = true;

        if (partitionLength > (uint)size)
        {
            throw Error("Bad VP8 partition length.");
        }

        _br = new Vp8BitReader(buf, offset, (int)partitionLength);
        offset += (int)partitionLength;
        size -= (int)partitionLength;

        _br.Get();  // colorspace
        _br.Get();  // clamp type
        if (!ParseSegmentHeader())
        {
            throw Error("Cannot parse VP8 segment header.");
        }

        if (!ParseFilterHeader())
        {
            throw Error("Cannot parse VP8 filter header.");
        }

        ParsePartitions(offset, size);
        ParseQuant();
        _br.Get();  // ignore the value of update_proba_
        ParseProba();
    }

    // port: ParseSegmentHeader (Paragraph 9.3)
    private bool ParseSegmentHeader()
    {
        _useSegment = _br.Get() != 0;
        if (_useSegment)
        {
            _updateMap = _br.Get() != 0;
            if (_br.Get() != 0)
            {
                // update data
                _absoluteDelta = _br.Get() != 0;
                for (int s = 0; s < NumMbSegments; ++s)
                {
                    _quantizer[s] = (sbyte)(_br.Get() != 0 ? _br.GetSignedValue(7) : 0);
                }

                for (int s = 0; s < NumMbSegments; ++s)
                {
                    _filterStrength[s] = (sbyte)(_br.Get() != 0 ? _br.GetSignedValue(6) : 0);
                }
            }

            if (_updateMap)
            {
                for (int s = 0; s < MbFeatureTreeProbs; ++s)
                {
                    _segmentProbas[s] = (byte)(_br.Get() != 0 ? _br.GetValue(8) : 255);
                }
            }
        }
        else
        {
            _updateMap = false;
        }

        return !_br.Eof;
    }

    // port: ParseFilterHeader (Paragraph 9.4)
    private bool ParseFilterHeader()
    {
        _simple = _br.Get() != 0;
        _level = _br.GetValue(6);
        _sharpness = _br.GetValue(3);
        _useLfDelta = _br.Get() != 0;
        if (_useLfDelta)
        {
            if (_br.Get() != 0)
            {
                // update lf-delta?
                for (int i = 0; i < NumRefLfDeltas; ++i)
                {
                    if (_br.Get() != 0)
                    {
                        _refLfDelta[i] = _br.GetSignedValue(6);
                    }
                }

                for (int i = 0; i < NumModeLfDeltas; ++i)
                {
                    if (_br.Get() != 0)
                    {
                        _modeLfDelta[i] = _br.GetSignedValue(6);
                    }
                }
            }
        }

        _filterType = _level == 0 ? 0 : _simple ? 1 : 2;
        return !_br.Eof;
    }

    // port: ParsePartitions (Paragraph 9.5)
    private void ParsePartitions(int offset, int size)
    {
        byte[] buf = _data;
        int sz = offset;
        int bufEnd = offset + size;
        _numPartsMinusOne = (1 << _br.GetValue(2)) - 1;
        int lastPart = _numPartsMinusOne;
        if (size < 3 * lastPart)
        {
            throw Error("Cannot parse VP8 partitions.");
        }

        int partStart = offset + (lastPart * 3);
        int sizeLeft = size - (lastPart * 3);
        for (int p = 0; p < lastPart; ++p)
        {
            int psize = buf[sz] | (buf[sz + 1] << 8) | (buf[sz + 2] << 16);
            if (psize > sizeLeft)
            {
                psize = sizeLeft;
            }

            _parts[p] = new Vp8BitReader(buf, partStart, psize);
            partStart += psize;
            sizeLeft -= psize;
            sz += 3;
        }

        _parts[lastPart] = new Vp8BitReader(buf, partStart, sizeLeft);
        if (partStart >= bufEnd)
        {
            throw Error("Truncated VP8 partition data.");
        }
    }

    // port: VP8ParseQuant (Paragraph 9.6)
    private void ParseQuant()
    {
        int baseQ0 = _br.GetValue(7);
        int dqy1Dc = _br.Get() != 0 ? _br.GetSignedValue(4) : 0;
        int dqy2Dc = _br.Get() != 0 ? _br.GetSignedValue(4) : 0;
        int dqy2Ac = _br.Get() != 0 ? _br.GetSignedValue(4) : 0;
        int dquvDc = _br.Get() != 0 ? _br.GetSignedValue(4) : 0;
        int dquvAc = _br.Get() != 0 ? _br.GetSignedValue(4) : 0;

        for (int i = 0; i < NumMbSegments; ++i)
        {
            int q;
            if (_useSegment)
            {
                q = _quantizer[i];
                if (!_absoluteDelta)
                {
                    q += baseQ0;
                }
            }
            else
            {
                if (i > 0)
                {
                    _dqm[i] = _dqm[0];
                    continue;
                }

                q = baseQ0;
            }

            QuantMatrix m;
            m.Y1Dc = Vp8Tables.DcTable[Clip(q + dqy1Dc, 127)];
            m.Y1Ac = Vp8Tables.AcTable[Clip(q + 0, 127)];
            m.Y2Dc = Vp8Tables.DcTable[Clip(q + dqy2Dc, 127)] * 2;
            // For all x in [0..284], x*155/100 is bitwise equal to (x*101581) >> 16.
            m.Y2Ac = (Vp8Tables.AcTable[Clip(q + dqy2Ac, 127)] * 101581) >> 16;
            if (m.Y2Ac < 8)
            {
                m.Y2Ac = 8;
            }

            m.UvDc = Vp8Tables.DcTable[Clip(q + dquvDc, 117)];
            m.UvAc = Vp8Tables.AcTable[Clip(q + dquvAc, 127)];
            _dqm[i] = m;
        }

        static int Clip(int v, int max) => v < 0 ? 0 : v > max ? max : v;
    }

    // port: VP8ParseProba
    private void ParseProba()
    {
        ReadOnlySpan<byte> update = Vp8Tables.CoeffsUpdateProba;
        ReadOnlySpan<byte> defaults = Vp8Tables.CoeffsProba0;
        for (int i = 0; i < _bands.Length; ++i)
        {
            _bands[i] = (byte)(_br.GetBit(update[i]) != 0 ? _br.GetValue(8) : defaults[i]);
        }

        _useSkipProba = _br.Get() != 0;
        if (_useSkipProba)
        {
            _skipP = _br.GetValue(8);
        }
    }

    // port: PrecomputeFilterStrengths (frame_dec.c)
    private void PrecomputeFilterStrengths()
    {
        if (_filterType == 0)
        {
            return;
        }

        for (int s = 0; s < NumMbSegments; ++s)
        {
            int baseLevel;
            if (_useSegment)
            {
                baseLevel = _filterStrength[s];
                if (!_absoluteDelta)
                {
                    baseLevel += _level;
                }
            }
            else
            {
                baseLevel = _level;
            }

            for (int i4x4 = 0; i4x4 <= 1; ++i4x4)
            {
                FilterInfo info = default;
                int level = baseLevel;
                if (_useLfDelta)
                {
                    level += _refLfDelta[0];
                    if (i4x4 != 0)
                    {
                        level += _modeLfDelta[0];
                    }
                }

                level = level < 0 ? 0 : level > 63 ? 63 : level;
                if (level > 0)
                {
                    int ilevel = level;
                    if (_sharpness > 0)
                    {
                        if (_sharpness > 4)
                        {
                            ilevel >>= 2;
                        }
                        else
                        {
                            ilevel >>= 1;
                        }

                        if (ilevel > 9 - _sharpness)
                        {
                            ilevel = 9 - _sharpness;
                        }
                    }

                    if (ilevel < 1)
                    {
                        ilevel = 1;
                    }

                    info.ILevel = (byte)ilevel;
                    info.Limit = (byte)((2 * level) + ilevel);
                    info.HevThresh = (byte)(level >= 40 ? 2 : level >= 15 ? 1 : 0);
                }
                else
                {
                    info.Limit = 0;  // no filtering
                }

                info.Inner = i4x4 != 0;
                _fStrengths[s, i4x4] = info;
            }
        }
    }

    // port: AllocateMemory (frame_dec.c), with full-frame planes instead of row caches
    private void AllocateMemory()
    {
        _intraT = new byte[4 * _mbW];   // initialized to B_DC_PRED (0)
        _mbData = CreateMacroblockData();

        _mbInfo = new NzInfo[_mbW + 1];
        _fInfo = new FilterInfo[_mbW * _mbH];
        _topY = new byte[16 * _mbW];
        _topU = new byte[8 * _mbW];
        _topV = new byte[8 * _mbW];
        _yStride = 16 * _mbW;
        _uvStride = 8 * _mbW;
        _planeY = new byte[checked(_yStride * 16 * _mbH)];
        _planeU = new byte[checked(_uvStride * 8 * _mbH)];
        _planeV = new byte[_planeU.Length];
        InitScanline();
    }

    // ------------------------------------------------------------------ parsing

    // port: VP8InitScanline
    private void InitScanline()
    {
        _mbInfo[0] = default;
        _intraL.AsSpan().Fill(BDcPred);
    }

    // port: ParseFrame
    // Reconstruction reads its top context from separate buffers and filtering only touches reconstructed rows,
    // so a worker can reconstruct and filter row y - 1 (in raster order) while this thread parses row y into the
    // other of two macroblock buffers (like libwebp's multi-threaded decoding).
    private void ParseAndFilterFrame(int maxDegreeOfParallelism)
    {
        if (!Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)_mbW * _mbH, 256) || _mbH <= 2)
        {
            for (int mbY = 0; mbY < _mbH; ++mbY)
            {
                ParseRow(mbY);
                ReconstructRow(mbY, _mbData);
            }

            FilterFrame();
            return;
        }

        MacroblockData[][] rows = [_mbData, CreateMacroblockData()];
        var parsed = new Parallelism.RowGate();
        var reconstructed = new Parallelism.RowGate();
        Task worker = Task.Run(() =>
        {
            try
            {
                for (int mbY = 0; mbY < _mbH && parsed.WaitFor(mbY); ++mbY)
                {
                    ReconstructRow(mbY, rows[mbY & 1]);
                    FilterRow(mbY);
                    reconstructed.Publish(mbY + 1);
                }
            }
            finally
            {
                // Lets the parser stop waiting if this loop ends early.
                reconstructed.Abort();
            }
        });

        try
        {
            // Buffer y & 1 is free once row y - 2 has been reconstructed.
            for (int mbY = 0; mbY < _mbH && (mbY < 2 || reconstructed.WaitFor(mbY - 2)); ++mbY)
            {
                _mbData = rows[mbY & 1];
                ParseRow(mbY);
                parsed.Publish(mbY + 1);
            }
        }
        catch
        {
            parsed.Abort();
            try { worker.Wait(); } catch (AggregateException) { }
            throw;
        }

        worker.GetAwaiter().GetResult();
    }

    private MacroblockData[] CreateMacroblockData()
    {
        var data = new MacroblockData[_mbW];
        for (int i = 0; i < _mbW; i++)
        {
            data[i] = new MacroblockData();
        }

        return data;
    }

    // Parses the intra modes and residuals of one macroblock row into _mbData.
    private void ParseRow(int mbY)
    {
        Vp8BitReader tokenBr = _parts[mbY & _numPartsMinusOne];
        if (!ParseIntraModeRow())
        {
            throw Error("Premature end of partition 0.");
        }

        for (int mbX = 0; mbX < _mbW; ++mbX)
        {
            if (!DecodeMB(mbX, mbY, tokenBr))
            {
                throw Error("Premature end of VP8 data.");
            }
        }

        InitScanline();  // prepare for next scanline
    }

    // port: VP8ParseIntraModeRow
    private bool ParseIntraModeRow()
    {
        for (int mbX = 0; mbX < _mbW; ++mbX)
        {
            ParseIntraMode(mbX);
        }

        return !_br.Eof;
    }

    // port: ParseIntraMode (hardcoded tree variant)
    private void ParseIntraMode(int mbX)
    {
        Vp8BitReader br = _br;
        Span<byte> top = _intraT.AsSpan(4 * mbX, 4);
        Span<byte> left = _intraL;
        MacroblockData block = _mbData[mbX];

        if (_updateMap)
        {
            block.Segment = br.GetBit(_segmentProbas[0]) == 0
                ? (byte)br.GetBit(_segmentProbas[1])
                : (byte)(br.GetBit(_segmentProbas[2]) + 2);
        }
        else
        {
            block.Segment = 0;  // default for intra
        }

        if (_useSkipProba)
        {
            block.Skip = br.GetBit(_skipP) != 0;
        }

        block.IsI4x4 = br.GetBit(145) == 0;
        if (!block.IsI4x4)
        {
            // Hardcoded 16x16 intra-mode decision tree.
            int ymode = br.GetBit(156) != 0
                ? (br.GetBit(128) != 0 ? TmPred : HPred)
                : (br.GetBit(163) != 0 ? VPred : DcPred);
            block.IModes[0] = (byte)ymode;
            top.Fill((byte)ymode);
            left.Fill((byte)ymode);
        }
        else
        {
            ReadOnlySpan<byte> bmodesProba = Vp8Tables.BModesProba;
            int modes = 0;
            for (int y = 0; y < 4; ++y)
            {
                int ymode = left[y];
                for (int x = 0; x < 4; ++x)
                {
                    ReadOnlySpan<byte> prob = bmodesProba.Slice(((top[x] * 10) + ymode) * 9, 9);
                    ymode = br.GetBit(prob[0]) == 0 ? BDcPred :
                        br.GetBit(prob[1]) == 0 ? BTmPred :
                        br.GetBit(prob[2]) == 0 ? BVePred :
                        br.GetBit(prob[3]) == 0
                            ? (br.GetBit(prob[4]) == 0 ? BHePred : (br.GetBit(prob[5]) == 0 ? BRdPred : BVrPred))
                            : (br.GetBit(prob[6]) == 0 ? BLdPred :
                                (br.GetBit(prob[7]) == 0 ? BVlPred : (br.GetBit(prob[8]) == 0 ? BHdPred : BHuPred)));
                    top[x] = (byte)ymode;
                }

                top.CopyTo(block.IModes.AsSpan(modes, 4));
                modes += 4;
                left[y] = (byte)ymode;
            }
        }

        // Hardcoded UVMode decision tree
        block.UvMode = (byte)(br.GetBit(142) == 0 ? DcPred
            : br.GetBit(114) == 0 ? VPred
            : br.GetBit(183) != 0 ? TmPred : HPred);
    }

    // port: GetLargeValue (Paragraph 13.2)
    private static int GetLargeValue(Vp8BitReader br, ReadOnlySpan<byte> p)
    {
        int v;
        if (br.GetBit(p[3]) == 0)
        {
            if (br.GetBit(p[4]) == 0)
            {
                v = 2;
            }
            else
            {
                v = 3 + br.GetBit(p[5]);
            }
        }
        else
        {
            if (br.GetBit(p[6]) == 0)
            {
                if (br.GetBit(p[7]) == 0)
                {
                    v = 5 + br.GetBit(159);
                }
                else
                {
                    v = 7 + (2 * br.GetBit(165));
                    v += br.GetBit(145);
                }
            }
            else
            {
                int bit1 = br.GetBit(p[8]);
                int bit0 = br.GetBit(p[9 + bit1]);
                int cat = (2 * bit1) + bit0;
                ReadOnlySpan<byte> tab = cat switch
                {
                    0 => Vp8Tables.Cat3,
                    1 => Vp8Tables.Cat4,
                    2 => Vp8Tables.Cat5,
                    _ => Vp8Tables.Cat6,
                };
                v = 0;
                for (int i = 0; tab[i] != 0; ++i)
                {
                    v += v + br.GetBit(tab[i]);
                }

                v += 3 + (8 << cat);
            }
        }

        return v;
    }

    // Offset of the probabilities for coefficient type 't', coefficient index 'n' and context 'ctx'.
    private static int ProbaOffset(int t, int n, int ctx) => ((((t * NumBands) + Vp8Tables.Bands[n]) * NumCtx) + ctx) * NumProbas;

    // port: GetCoeffsFast. Returns the position of the last non-zero coeff plus one.
    private int GetCoeffs(Vp8BitReader br, int type, int ctx, int dqDc, int dqAc, int n, Span<short> output)
    {
        byte[] bands = _bands;
        int p = ProbaOffset(type, n, ctx);
        for (; n < 16; ++n)
        {
            if (br.GetBit(bands[p]) == 0)
            {
                return n;  // previous coeff was last non-zero coeff
            }

            while (br.GetBit(bands[p + 1]) == 0)
            {
                // sequence of zero coeffs
                ++n;
                if (n == 16)
                {
                    return 16;
                }

                p = ProbaOffset(type, n, 0);
            }

            // non zero coeff
            int pCtx = ProbaOffset(type, n + 1, 0);
            int v;
            if (br.GetBit(bands[p + 2]) == 0)
            {
                v = 1;
                p = pCtx + NumProbas;      // context 1
            }
            else
            {
                v = GetLargeValue(br, bands.AsSpan(p, NumProbas));
                p = pCtx + (2 * NumProbas); // context 2
            }

            output[Vp8Tables.Zigzag[n]] = (short)(br.GetSigned(v) * (n > 0 ? dqAc : dqDc));
        }

        return 16;
    }

    private static uint NzCodeBits(uint nzCoeffs, int nz, int dcNz)
    {
        nzCoeffs <<= 2;
        nzCoeffs |= (uint)(nz > 3 ? 3 : nz > 1 ? 2 : dcNz);
        return nzCoeffs;
    }

    // port: ParseResiduals. Returns true if the macroblock has no non-zero coefficient.
    private bool ParseResiduals(int mbX, Vp8BitReader tokenBr)
    {
        MacroblockData block = _mbData[mbX];
        ref NzInfo mb = ref _mbInfo[1 + mbX];
        ref NzInfo leftMb = ref _mbInfo[0];
        QuantMatrix q = _dqm[block.Segment];
        Span<short> coeffs = block.Coeffs;
        int dst = 0;
        uint nonZeroY = 0;
        uint nonZeroUv = 0;
        int first;
        int acType;

        coeffs.Clear();
        if (!block.IsI4x4)
        {
            // parse DC
            Span<short> dc = stackalloc short[16];
            dc.Clear();
            int ctx = mb.NzDc + leftMb.NzDc;
            int nz = GetCoeffs(tokenBr, 1, ctx, q.Y2Dc, q.Y2Ac, 0, dc);
            mb.NzDc = leftMb.NzDc = (byte)(nz > 0 ? 1 : 0);
            if (nz > 1)
            {
                // more than just the DC -> perform the full transform
                TransformWHT(dc, coeffs);
            }
            else
            {
                // only DC is non-zero -> inlined simplified transform
                int dc0 = (dc[0] + 3) >> 3;
                for (int i = 0; i < 16 * 16; i += 16)
                {
                    coeffs[i] = (short)dc0;
                }
            }

            first = 1;
            acType = 0;
        }
        else
        {
            first = 0;
            acType = 3;
        }

        uint tnz = (uint)(mb.Nz & 0x0f);
        uint lnz = (uint)(leftMb.Nz & 0x0f);
        for (int y = 0; y < 4; ++y)
        {
            int l = (int)(lnz & 1);
            uint nzCoeffs = 0;
            for (int x = 0; x < 4; ++x)
            {
                int ctx = l + (int)(tnz & 1);
                Span<short> block16 = coeffs.Slice(dst, 16);
                int nz = GetCoeffs(tokenBr, acType, ctx, q.Y1Dc, q.Y1Ac, first, block16);
                l = nz > first ? 1 : 0;
                tnz = (tnz >> 1) | ((uint)l << 7);
                nzCoeffs = NzCodeBits(nzCoeffs, nz, block16[0] != 0 ? 1 : 0);
                dst += 16;
            }

            tnz >>= 4;
            lnz = (lnz >> 1) | ((uint)l << 7);
            nonZeroY = (nonZeroY << 8) | nzCoeffs;
        }

        uint outTNz = tnz;
        uint outLNz = lnz >> 4;

        for (int ch = 0; ch < 4; ch += 2)
        {
            uint nzCoeffs = 0;
            tnz = (uint)(mb.Nz >> (4 + ch));
            lnz = (uint)(leftMb.Nz >> (4 + ch));
            for (int y = 0; y < 2; ++y)
            {
                int l = (int)(lnz & 1);
                for (int x = 0; x < 2; ++x)
                {
                    int ctx = l + (int)(tnz & 1);
                    Span<short> block16 = coeffs.Slice(dst, 16);
                    int nz = GetCoeffs(tokenBr, 2, ctx, q.UvDc, q.UvAc, 0, block16);
                    l = nz > 0 ? 1 : 0;
                    tnz = (tnz >> 1) | ((uint)l << 3);
                    nzCoeffs = NzCodeBits(nzCoeffs, nz, block16[0] != 0 ? 1 : 0);
                    dst += 16;
                }

                tnz >>= 2;
                lnz = (lnz >> 1) | ((uint)l << 5);
            }

            // Note: we don't really need the per-4x4 details for U/V blocks.
            nonZeroUv |= nzCoeffs << (4 * ch);
            outTNz |= (tnz << 4) << ch;
            outLNz |= (lnz & 0xf0) << ch;
        }

        mb.Nz = (byte)outTNz;
        leftMb.Nz = (byte)outLNz;

        block.NonZeroY = nonZeroY;
        block.NonZeroUv = nonZeroUv;
        return (nonZeroY | nonZeroUv) == 0;
    }

    // port: VP8DecodeMB
    private bool DecodeMB(int mbX, int mbY, Vp8BitReader tokenBr)
    {
        MacroblockData block = _mbData[mbX];
        bool skip = _useSkipProba && block.Skip;

        if (!skip)
        {
            skip = ParseResiduals(mbX, tokenBr);
        }
        else
        {
            _mbInfo[0].Nz = _mbInfo[1 + mbX].Nz = 0;
            if (!block.IsI4x4)
            {
                _mbInfo[0].NzDc = _mbInfo[1 + mbX].NzDc = 0;
            }

            block.NonZeroY = 0;
            block.NonZeroUv = 0;
        }

        if (_filterType > 0)
        {
            // store filter info
            FilterInfo finfo = _fStrengths[block.Segment, block.IsI4x4 ? 1 : 0];
            finfo.Inner |= !skip;
            _fInfo[(mbY * _mbW) + mbX] = finfo;
        }

        return !tokenBr.Eof;
    }

    // ------------------------------------------------------------------ reconstruction

    private static int CheckMode(int mbX, int mbY, int mode)
    {
        if (mode == BDcPred)
        {
            if (mbX == 0)
            {
                return mbY == 0 ? BDcPredNoTopLeft : BDcPredNoLeft;
            }

            return mbY == 0 ? BDcPredNoTop : BDcPred;
        }

        return mode;
    }

    private static void DoTransform(uint bits, ReadOnlySpan<short> src, byte[] dst, int off)
    {
        switch (bits >> 30)
        {
            case 3:
                TransformOne(src, dst, off);
                break;
            case 2:
                TransformAC3(src, dst, off);
                break;
            case 1:
                TransformDC(src, dst, off);
                break;
        }
    }

    private static void DoUVTransform(uint bits, ReadOnlySpan<short> src, byte[] dst, int off)
    {
        if ((bits & 0xff) != 0)
        {
            // any non-zero coeff at all?
            if ((bits & 0xaa) != 0)
            {
                // any non-zero AC coefficient?
                TransformUV(src, dst, off);  // note we don't use the AC3 variant for U/V
            }
            else
            {
                TransformDCUV(src, dst, off);
            }
        }
    }

    // port: ReconstructRow
    private void ReconstructRow(int mbY, MacroblockData[] row)
    {
        byte[] b = _yuvB;

        // Initialize left-most block.
        for (int j = 0; j < 16; ++j)
        {
            b[YOff + (j * Bps) - 1] = 129;
        }

        for (int j = 0; j < 8; ++j)
        {
            b[UOff + (j * Bps) - 1] = 129;
            b[VOff + (j * Bps) - 1] = 129;
        }

        // Init top-left sample on left column too.
        if (mbY > 0)
        {
            b[YOff - 1 - Bps] = b[UOff - 1 - Bps] = b[VOff - 1 - Bps] = 129;
        }
        else
        {
            // we only need to do this init once at block (0,0).
            // Afterward, it remains valid for the whole topmost row.
            b.AsSpan(YOff - Bps - 1, 16 + 4 + 1).Fill(127);
            b.AsSpan(UOff - Bps - 1, 8 + 1).Fill(127);
            b.AsSpan(VOff - Bps - 1, 8 + 1).Fill(127);
        }

        // Reconstruct one row.
        for (int mbX = 0; mbX < _mbW; ++mbX)
        {
            MacroblockData block = row[mbX];

            // Rotate in the left samples from previously decoded block. We move four
            // pixels at a time for alignment reason, and because of in-loop filter.
            if (mbX > 0)
            {
                for (int j = -1; j < 16; ++j)
                {
                    Copy32b(b, YOff + (j * Bps) + 12, YOff + (j * Bps) - 4);
                }

                for (int j = -1; j < 8; ++j)
                {
                    Copy32b(b, UOff + (j * Bps) + 4, UOff + (j * Bps) - 4);
                    Copy32b(b, VOff + (j * Bps) + 4, VOff + (j * Bps) - 4);
                }
            }

            // bring top samples into the cache
            ReadOnlySpan<short> coeffs = block.Coeffs;
            uint bits = block.NonZeroY;

            if (mbY > 0)
            {
                Array.Copy(_topY, mbX * 16, b, YOff - Bps, 16);
                Array.Copy(_topU, mbX * 8, b, UOff - Bps, 8);
                Array.Copy(_topV, mbX * 8, b, VOff - Bps, 8);
            }

            // predict and add residuals
            if (block.IsI4x4)
            {
                // 4x4
                int topRight = YOff - Bps + 16;
                if (mbY > 0)
                {
                    if (mbX >= _mbW - 1)
                    {
                        // on rightmost border
                        b.AsSpan(topRight, 4).Fill(_topY[(mbX * 16) + 15]);
                    }
                    else
                    {
                        Array.Copy(_topY, (mbX + 1) * 16, b, topRight, 4);
                    }
                }

                // replicate the top-right pixels below
                Copy32b(b, topRight, topRight + (4 * Bps));
                Copy32b(b, topRight, topRight + (8 * Bps));
                Copy32b(b, topRight, topRight + (12 * Bps));

                // predict and add residuals for all 4x4 blocks in turn.
                for (int n = 0; n < 16; ++n, bits <<= 2)
                {
                    int dst = YOff + Scan[n];
                    PredLuma4(block.IModes[n], b, dst);
                    DoTransform(bits, coeffs.Slice(n * 16, 16), b, dst);
                }
            }
            else
            {
                // 16x16
                int predFunc = CheckMode(mbX, mbY, block.IModes[0]);
                PredLuma16(predFunc, b, YOff);
                if (bits != 0)
                {
                    for (int n = 0; n < 16; ++n, bits <<= 2)
                    {
                        DoTransform(bits, coeffs.Slice(n * 16, 16), b, YOff + Scan[n]);
                    }
                }
            }

            {
                // Chroma
                uint bitsUv = block.NonZeroUv;
                int predFunc = CheckMode(mbX, mbY, block.UvMode);
                PredChroma8(predFunc, b, UOff);
                PredChroma8(predFunc, b, VOff);
                DoUVTransform(bitsUv >> 0, coeffs.Slice(16 * 16, 64), b, UOff);
                DoUVTransform(bitsUv >> 8, coeffs.Slice(20 * 16, 64), b, VOff);
            }

            // stash away top samples for next block
            if (mbY < _mbH - 1)
            {
                Array.Copy(b, YOff + (15 * Bps), _topY, mbX * 16, 16);
                Array.Copy(b, UOff + (7 * Bps), _topU, mbX * 8, 8);
                Array.Copy(b, VOff + (7 * Bps), _topV, mbX * 8, 8);
            }

            // Transfer reconstructed samples from the work buffer to the planes.
            int yOut = (mbY * 16 * _yStride) + (mbX * 16);
            int uvOut = (mbY * 8 * _uvStride) + (mbX * 8);
            for (int j = 0; j < 16; ++j)
            {
                Array.Copy(b, YOff + (j * Bps), _planeY, yOut + (j * _yStride), 16);
            }

            for (int j = 0; j < 8; ++j)
            {
                Array.Copy(b, UOff + (j * Bps), _planeU, uvOut + (j * _uvStride), 8);
                Array.Copy(b, VOff + (j * Bps), _planeV, uvOut + (j * _uvStride), 8);
            }
        }

        static void Copy32b(byte[] buf, int src, int dst) => Array.Copy(buf, src, buf, dst, 4);
    }

    // ------------------------------------------------------------------ filtering

    // port: FilterRow / DoFilter. Filtering row y after reconstructing it is the raster order of FinishRow; the
    // filters of row y touch rows 16y-3..16y+15, so they never meet the reconstruction of later rows.
    private void FilterFrame()
    {
        for (int mbY = 0; mbY < _mbH; ++mbY)
        {
            FilterRow(mbY);
        }
    }

    private void FilterRow(int mbY)
    {
        if (_filterType == 0)
        {
            return;
        }

        for (int mbX = 0; mbX < _mbW; ++mbX)
        {
            DoFilter(mbX, mbY);
        }
    }

    private void DoFilter(int mbX, int mbY)
    {
        int yBps = _yStride;
        FilterInfo fInfo = _fInfo[(mbY * _mbW) + mbX];
        int yDst = (mbY * 16 * yBps) + (mbX * 16);
        int ilevel = fInfo.ILevel;
        int limit = fInfo.Limit;
        if (limit == 0)
        {
            return;
        }

        byte[] y = _planeY;
        if (_filterType == 1)
        {
            // simple
            if (mbX > 0)
            {
                SimpleHFilter16(y, yDst, yBps, limit + 4);
            }

            if (fInfo.Inner)
            {
                SimpleHFilter16i(y, yDst, yBps, limit);
            }

            if (mbY > 0)
            {
                SimpleVFilter16(y, yDst, yBps, limit + 4);
            }

            if (fInfo.Inner)
            {
                SimpleVFilter16i(y, yDst, yBps, limit);
            }
        }
        else
        {
            // complex
            int uvBps = _uvStride;
            int uvDst = (mbY * 8 * uvBps) + (mbX * 8);
            byte[] u = _planeU;
            byte[] v = _planeV;
            int hevThresh = fInfo.HevThresh;
            if (mbX > 0)
            {
                HFilter16(y, yDst, yBps, limit + 4, ilevel, hevThresh);
                HFilter8(u, uvDst, v, uvDst, uvBps, limit + 4, ilevel, hevThresh);
            }

            if (fInfo.Inner)
            {
                HFilter16i(y, yDst, yBps, limit, ilevel, hevThresh);
                HFilter8i(u, uvDst, v, uvDst, uvBps, limit, ilevel, hevThresh);
            }

            if (mbY > 0)
            {
                VFilter16(y, yDst, yBps, limit + 4, ilevel, hevThresh);
                VFilter8(u, uvDst, v, uvDst, uvBps, limit + 4, ilevel, hevThresh);
            }

            if (fInfo.Inner)
            {
                VFilter16i(y, yDst, yBps, limit, ilevel, hevThresh);
                VFilter8i(u, uvDst, v, uvDst, uvBps, limit, ilevel, hevThresh);
            }
        }
    }

    // ------------------------------------------------------------------ types

    private struct QuantMatrix
    {
        public int Y1Dc, Y1Ac, Y2Dc, Y2Ac, UvDc, UvAc;
    }

    private struct NzInfo
    {
        public byte Nz;     // non-zero AC/DC coeffs (4bit for luma + 4bit for chroma)
        public byte NzDc;   // non-zero DC coeff (1bit)
    }

    private struct FilterInfo
    {
        public byte Limit;      // filter limit in [3..189], or 0 if no filtering
        public byte ILevel;     // inner limit in [1..63]
        public bool Inner;      // do inner filtering?
        public byte HevThresh;  // high edge variance threshold in [0..2]
    }

    private sealed class MacroblockData
    {
        public readonly short[] Coeffs = new short[384];  // 384 coeffs = (16+4+4) * 4*4
        public bool IsI4x4;
        public readonly byte[] IModes = new byte[16];     // one 16x16 mode (#0) or sixteen 4x4 modes
        public byte UvMode;
        public uint NonZeroY;
        public uint NonZeroUv;
        public bool Skip;
        public byte Segment;
    }
}
