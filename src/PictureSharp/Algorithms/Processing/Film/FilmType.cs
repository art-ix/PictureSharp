namespace PictureSharp.Processing;

/// <summary>The kind of film a negative is on, for <see cref="ImageProcessingContext.DevelopNegative(NegativeOptions?)"/>.</summary>
public enum FilmType
{
    /// <summary>Color negative film (C-41) with an orange mask; each channel is inverted separately.</summary>
    ColorNegative,
    /// <summary>Black-and-white negative film; the luma is inverted and the result is gray.</summary>
    BlackAndWhiteNegative,
}
