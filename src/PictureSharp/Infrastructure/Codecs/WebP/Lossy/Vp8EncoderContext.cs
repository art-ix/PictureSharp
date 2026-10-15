using System.Buffers;
using PictureSharp.Algorithms.ColorConversion;

namespace PictureSharp.Infrastructure.Codecs.WebP.Lossy;

internal struct Vp8Macroblock
{
    public byte Type, UvMode, Segment, Alpha;
    public bool Skip;
}

internal sealed class Vp8Segment
{
    public int Alpha, Beta, Quant, FilterStrength, MaxEdge, MinDisto;
    public int LambdaI16, LambdaI4, LambdaUv, LambdaMode, TLambda;
    public int LambdaTrellisI16, LambdaTrellisI4, LambdaTrellisUv;
    public long I4Penalty;
    public Vp8Matrix Y1 { get; } = new();
    public Vp8Matrix Y2 { get; } = new();
    public Vp8Matrix Uv { get; } = new();
}

internal sealed class Vp8EncoderContext : IDisposable
{
    public Vp8EncoderConfig Config { get; }
    public CancellationToken CancellationToken { get; init; }
    public RgbToYuv Source { get; }
    public int MbWidth { get; }
    public int MbHeight { get; }
    public bool UseErrorDiffusion => Config.Options.Quality <= 98 || Config.Options.Passes > 1;
    public int MbCount => MbWidth * MbHeight;
    public int PredStride => 4 * MbWidth + 1;
    public Vp8Macroblock[] Blocks { get; }
    public byte[] Modes { get; }
    public byte[] TopY { get; }
    public byte[] TopUv { get; }
    public uint[] Nonzero { get; }
    public sbyte[] TopError { get; }
    public Vp8Segment[] Segments { get; } = [new(), new(), new(), new()];
    public int SegmentCount, Alpha, UvAlpha, BaseQuant, DeltaUvDc, DeltaUvAc, FilterLevel, FilterSharpness;

    // port: src/enc/webp_enc.c InitVP8Encoder, ResetBoundaryPredictions
    public Vp8EncoderContext(RgbToYuv source, Vp8EncoderConfig config)
    {
        Source = source; Config = config;
        MbWidth = (source.Width + 15) >> 4; MbHeight = (source.Height + 15) >> 4;
        SegmentCount = config.Segments;
        Blocks = ArrayPool<Vp8Macroblock>.Shared.Rent(MbCount);
        Blocks.AsSpan(0, MbCount).Clear();
        Modes = ArrayPool<byte>.Shared.Rent(PredStride * (4 * MbHeight + 1));
        Modes.AsSpan(0, PredStride * (4 * MbHeight + 1)).Clear();
        TopY = ArrayPool<byte>.Shared.Rent(16 * MbWidth);
        TopUv = ArrayPool<byte>.Shared.Rent(16 * MbWidth);
        Nonzero = ArrayPool<uint>.Shared.Rent(MbWidth + 1);
        TopError = ArrayPool<sbyte>.Shared.Rent(4 * MbWidth);
    }

    public void Dispose()
    {
        ArrayPool<Vp8Macroblock>.Shared.Return(Blocks);
        ArrayPool<byte>.Shared.Return(Modes); ArrayPool<byte>.Shared.Return(TopY); ArrayPool<byte>.Shared.Return(TopUv);
        ArrayPool<uint>.Shared.Return(Nonzero); ArrayPool<sbyte>.Shared.Return(TopError);
    }
}
