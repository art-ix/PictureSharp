namespace PictureSharp.Formats.Png;

/// <summary>PNG-specific decoding settings.</summary>
public sealed record PngDecoderOptions
{
    /// <summary>Reject invalid chunk CRCs. Default: false.</summary>
    public bool StrictChecksums { get; init; }
}
