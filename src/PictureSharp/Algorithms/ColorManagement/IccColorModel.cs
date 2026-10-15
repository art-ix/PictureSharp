namespace PictureSharp.Algorithms.ColorManagement;

/// <summary>
/// A matrix/TRC color model: tone curves to linear light and, for RGB, the colorant matrix to XYZ (D50).
/// Gray models have only <see cref="Red"/> as their curve.
/// </summary>
internal sealed record IccColorModel(ToneCurve Red, ToneCurve? Green, ToneCurve? Blue, ColorMatrix? Colorants)
{
    public bool IsGray => Colorants is null;
}
