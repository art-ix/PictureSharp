namespace PictureSharp.Processing;

/// <summary>Which value of the mask image <see cref="ImageProcessingContext.ApplyMask(Image, System.Drawing.Point, MaskOptions?)"/> uses.</summary>
public enum MaskChannel
{
    /// <summary>The mask's alpha: opaque mask pixels keep the image, transparent ones remove it.</summary>
    Alpha = 0,
    /// <summary>The BT.601 luma of the mask's color, ignoring its alpha: white keeps the image, black removes it.</summary>
    Luminance,
}
