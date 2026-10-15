namespace PictureSharp.Formats.Ico;

/// <summary>Selection of an ICO variant; variants are independent images.</summary>
public sealed record IcoDecoderOptions
{
    /// <summary>Zero-based variant index; takes precedence over PreferredSize.</summary>
    public int? Index
    {
        get;
        init => field = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Index));
    }
    /// <summary>Preferred size (1–256 on each axis); null selects the largest supported variant.</summary>
    public System.Drawing.Size? PreferredSize
    {
        get;
        init => field = value is null || (value.Value.Width is >= 1 and <= 256 && value.Value.Height is >= 1 and <= 256)
            ? value : throw new ArgumentOutOfRangeException(nameof(PreferredSize));
    }
}
