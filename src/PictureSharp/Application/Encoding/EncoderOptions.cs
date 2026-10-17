namespace PictureSharp;

/// <summary>
/// Base record for format-specific encoder options. Values are checked when they are set, and a modified copy can be
/// made with <c>with</c>, e.g. <c>options with { Quality = 90 }</c>.
/// </summary>
public abstract record EncoderOptions
{
    /// <summary>Initializes options; derive a record per format, overriding <see cref="Format"/>.</summary>
    protected EncoderOptions()
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

    /// <summary>
    /// The formats a save with these options may use; the encoder of <see cref="Format"/> is taken from it. Default:
    /// null, which uses <c>ImageConfiguration.Default</c> at the time of the call, as
    /// <see cref="DecoderOptions.Configuration"/> does for loading.
    /// </summary>
    /// <example>
    /// <code>
    /// var formats = ImageConfiguration.Default.ToBuilder().AddFormat(new TgaFormat()).Build();
    /// image.Save("picture.tga", new TgaEncoderOptions { Configuration = formats });
    /// </code>
    /// </example>
    public ImageConfiguration? Configuration { get; init; }
}
