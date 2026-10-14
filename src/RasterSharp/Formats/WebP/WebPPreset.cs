namespace RasterSharp;

/// <summary>Content presets that tune WebP lossy encoding parameters (as in libwebp's WebPPreset).</summary>
public enum WebPPreset
{
    /// <summary>Default preset.</summary>
    Default = 0,
    /// <summary>Digital picture, like portrait, inner shot.</summary>
    Picture,
    /// <summary>Outdoor photograph, with natural lighting.</summary>
    Photo,
    /// <summary>Hand or line drawing, with high-contrast details.</summary>
    Drawing,
    /// <summary>Small-sized colorful images.</summary>
    Icon,
    /// <summary>Text-like images.</summary>
    Text,
}
