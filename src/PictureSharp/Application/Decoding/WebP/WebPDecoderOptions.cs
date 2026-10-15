namespace PictureSharp.Formats.WebP;

/// <summary>WebP-specific decoding settings.</summary>
public sealed record WebPDecoderOptions
{
    /// <summary>Use smooth chroma upsampling. Default: true.</summary>
    public bool FancyUpsampling { get; init; } = true;
    /// <summary>Skip the in-loop deblocking filter. Default: false.</summary>
    public bool BypassFiltering { get; init; }
}
