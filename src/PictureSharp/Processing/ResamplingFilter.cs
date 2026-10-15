namespace PictureSharp;

/// <summary>Interpolation kernels used for resizing.</summary>
public enum ResamplingFilter
{
    /// <summary>Nearest neighbour: fastest, blocky, keeps exact colors.</summary>
    Nearest = 0,
    /// <summary>Triangle (linear) filter.</summary>
    Bilinear,
    /// <summary>Cubic convolution with a = -0.5 (Catmull-Rom).</summary>
    Bicubic,
    /// <summary>Windowed sinc with three lobes: sharpest, slowest.</summary>
    Lanczos3,
    /// <summary>Box (area average) filter: each output pixel averages the source pixels it covers. Good for downscaling.</summary>
    Box,
    /// <summary>Hamming-windowed sinc with one lobe: sharper than bilinear at a similar cost.</summary>
    Hamming,
}
