namespace RasterSharp;

/// <summary>Compression used by a WebP file.</summary>
public enum WebPCompression
{
    /// <summary>Not a WebP file.</summary>
    None = 0,
    /// <summary>VP8 lossy bitstream (optionally with an ALPH chunk).</summary>
    Lossy,
    /// <summary>VP8L lossless bitstream.</summary>
    Lossless,
    /// <summary>Animation whose frames use both lossy and lossless bitstreams.</summary>
    Mixed,
}
