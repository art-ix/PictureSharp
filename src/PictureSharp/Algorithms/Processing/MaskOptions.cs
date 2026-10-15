namespace PictureSharp.Processing;

/// <summary>How <see cref="ImageProcessingContext.ApplyMask(Image, System.Drawing.Point, MaskOptions?)"/> reads the mask.</summary>
public sealed record MaskOptions
{
    /// <summary>Default options: the mask's alpha, not inverted, pixels outside the mask kept.</summary>
    public static MaskOptions Default { get; } = new();

    /// <summary>The mask value to use. Default: <see cref="MaskChannel.Alpha"/>.</summary>
    public MaskChannel Channel
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Channel), value, "Unknown mask channel.");
    }

    /// <summary>Whether to use 255 minus the mask value, so the mask removes what it would otherwise keep. Default: false.</summary>
    public bool Invert { get; init; }

    /// <summary>What happens to pixels the mask does not cover. Default: <see cref="MaskOutside.Keep"/>.</summary>
    public MaskOutside Outside
    {
        get;
        init => field = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(Outside), value, "Unknown outside policy.");
    }
}
