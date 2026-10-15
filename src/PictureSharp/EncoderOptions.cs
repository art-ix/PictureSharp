namespace PictureSharp;

/// <summary>
/// Base record for format-specific encoder options. Values are checked when they are set, and a modified copy can be
/// made with <c>with</c>, e.g. <c>options with { Quality = 90 }</c>.
/// </summary>
public abstract record EncoderOptions
{
    private protected EncoderOptions()
    {
    }

    /// <summary>The format produced by the encoder.</summary>
    public abstract ImageFormat Format { get; }

    /// <summary>Which metadata blocks to write when present on the image. Default: all.</summary>
    public MetadataKinds Metadata
    {
        get;
        init => field = (value & ~MetadataKinds.All) == 0 ? value : throw new ArgumentOutOfRangeException(nameof(Metadata), value, "Unknown metadata kind.");
    } = MetadataKinds.All;
}
