namespace PictureSharp.Formats.Gif;

/// <summary>How the GIF encoder reduces images with more colors than fit in a palette.</summary>
public enum GifQuantizer
{
    /// <summary>The default: <see cref="MedianCut"/>.</summary>
    Auto = 0,
    /// <summary>Median cut, as Pillow's <c>Image.quantize(method=MEDIANCUT)</c> (best quality).</summary>
    MedianCut,
    /// <summary>Fast octree, as Pillow's <c>Image.quantize(method=FASTOCTREE)</c> (faster).</summary>
    Octree,
}
