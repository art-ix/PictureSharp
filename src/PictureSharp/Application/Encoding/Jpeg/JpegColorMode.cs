namespace PictureSharp.Formats.Jpeg;

/// <summary>The number of components a JPEG encoder writes.</summary>
public enum JpegColorMode
{
    /// <summary>One luma component when every pixel is grey (R = G = B after compositing), otherwise YCbCr (the default).</summary>
    Auto,
    /// <summary>Always three YCbCr components with <see cref="JpegEncoderOptions.Subsampling"/>, also for grey pixels.</summary>
    Color,
    /// <summary>Always one component: the luma of each pixel (the Y of YCbCr), as libjpeg-turbo converts RGB to grayscale.</summary>
    Grayscale,
}
