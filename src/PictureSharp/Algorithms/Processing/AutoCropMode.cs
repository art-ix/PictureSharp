namespace PictureSharp.Processing;

/// <summary>What <see cref="ImageProcessingContext.AutoCrop(AutoCropOptions?)"/> trims away.</summary>
public enum AutoCropMode
{
    /// <summary>Transparent margins: pixels with alpha at or below <see cref="AutoCropOptions.AlphaThresholdNormalized"/>.</summary>
    Alpha = 0,
    /// <summary>
    /// Margins of a background color (<see cref="AutoCropOptions.Background"/>, by default the top-left pixel) within
    /// <see cref="AutoCropOptions.ToleranceNormalized"/>, like ImageMagick's <c>-trim</c> with <c>-fuzz</c>.
    /// </summary>
    Background,
}
