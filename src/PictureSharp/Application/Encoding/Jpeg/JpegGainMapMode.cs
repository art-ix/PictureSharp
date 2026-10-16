namespace PictureSharp.Formats.Jpeg;

/// <summary>Whether a JPEG encoder writes the HDR gain map attached to the image (ISO 21496-1, as libultrahdr).</summary>
public enum JpegGainMapMode
{
    /// <summary>Write an SDR JPEG without the map (the default).</summary>
    None,
    /// <summary>Write the map when one is attached with valid ISO 21496-1 metadata; otherwise write an SDR JPEG.</summary>
    Preserve,
    /// <summary>Write the map; throw when none is attached or it cannot be written.</summary>
    Require,
}
