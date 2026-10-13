namespace RasterSharp.Formats.Jpeg;

/// <summary>Parameters of one scan (SOS segment).</summary>
internal sealed class JpegScan
{
    public required JpegComponent[] Components { get; init; }
    public int Ss { get; init; }
    public int Se { get; init; }
    public int Ah { get; init; }
    public int Al { get; init; }
    public int RestartInterval { get; init; }
}
