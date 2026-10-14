namespace RasterSharp;

/// <summary>Dithering applied when an image is quantized to a palette.</summary>
public enum GifDither
{
    /// <summary>Map every pixel to the nearest palette color.</summary>
    None = 0,
    /// <summary>Floyd–Steinberg error diffusion (smoother gradients, larger files).</summary>
    FloydSteinberg,
}
