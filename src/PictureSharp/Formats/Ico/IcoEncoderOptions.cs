namespace PictureSharp;

/// <summary>Options for ICO output. Only the first frame of each supplied image is written.</summary>
public sealed record IcoEncoderOptions : EncoderOptions
{
    /// <inheritdoc/>
    public override ImageFormat Format => ImageFormat.Ico;

    /// <summary>Variant encoding. Default: PNG. DIB output uses 32-bit BGRA and an AND mask.</summary>
    public IcoEncoding Encoding
    {
        get;
        init => field = value is IcoEncoding.Png or IcoEncoding.Dib
            ? value : throw new ArgumentOutOfRangeException(nameof(Encoding));
    }
}
