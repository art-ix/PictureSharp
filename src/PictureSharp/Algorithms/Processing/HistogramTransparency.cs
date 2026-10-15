namespace PictureSharp.Processing;

/// <summary>Which pixels <see cref="ImageHistogram"/> counts with respect to transparency.</summary>
public enum HistogramTransparency
{
    /// <summary>Every pixel, whatever its alpha, as Pillow's <c>Image.histogram()</c>.</summary>
    Include = 0,
    /// <summary>Only pixels with alpha above 0; the color of fully transparent pixels is usually meaningless.</summary>
    Exclude,
}
