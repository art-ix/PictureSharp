using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Frames;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Develops a photographed film negative into a positive: measures it as
    /// <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/> does (unless
    /// <see cref="NegativeOptions.Calibration"/> is given), inverts it with <see cref="InvertNegative"/>, removes dust when
    /// <see cref="NegativeOptions.DustSize"/> asks for it, then removes color noise. The whole image is developed; crop
    /// it to the frame first, or set <see cref="NegativeOptions.Region"/> to measure only the frame. Alpha is kept.
    /// </summary>
    /// <remarks>
    /// The channels are inverted in the image's own color space, and its ICC profile or color description stays, so
    /// the positive keeps a wide gamut such as Display P3; call <see cref="ConvertToSrgb"/> afterwards when sRGB is
    /// needed. Converting the negative first would clip its strongly colored film base.
    /// </remarks>
    /// <example>
    /// <code>
    /// image.Mutate(x => x.Crop(frame).DevelopNegative());
    /// image.Mutate(x => x.DevelopNegative(new NegativeOptions { Region = frame, FilmBaseRegion = rebate }));
    /// </code>
    /// </example>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A region of <paramref name="options"/> is empty or not inside the image.</exception>
    public ImageProcessingContext DevelopNegative(NegativeOptions? options = null)
    {
        options ??= NegativeOptions.Default;
        CheckSingleFrame();
        NegativeCalibration calibration = options.Calibration ?? AnalyzeNegative(Target, options, _state.CancellationToken).Calibration;
        InvertNegative(calibration, options.Print);
        int dust = options.DustSize ?? Math.Max(2, (int)((0.005f * Math.Min(Target.Width, Target.Height)) + 0.5f));
        if (dust > 0)
        {
            RemoveDust(dust);
        }

        return calibration.FilmType == FilmType.ColorNegative ? ReduceColorNoise(options.ColorNoiseReduction) : this;
    }

    /// <summary>
    /// Inverts a negative with a known calibration, without measuring, cropping or cleaning: for each channel value the
    /// density above the film base <c>d = log10(FilmBase / max(L, 1/1024))</c> (L linear light) becomes
    /// <c>t = clamp(d / DensityRange, 0, 1.05)^MidtoneGamma</c>, then the print
    /// <c>10^(Contrast · (t − 1)) · 2^Exposure</c>, encoded to sRGB with the S curve of
    /// <see cref="NegativePrintOptions.Curve"/>. A black-and-white calibration first turns each pixel into its
    /// Rec. 709 luma, so the result is gray. Channel values are decoded with the sRGB transfer curve whatever the
    /// image's color space, which stays attached; every frame is inverted and alpha is kept.
    /// </summary>
    public ImageProcessingContext InvertNegative(NegativeCalibration calibration, NegativePrintOptions? print = null)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (calibration.FilmType == FilmType.BlackAndWhiteNegative)
        {
            ForEachPixelSpan(static pixels =>
            {
                foreach (ref Rgba32 p in pixels)
                {
                    byte y = (byte)GrayWorld.Luma(p);
                    p = new Rgba32(y, y, y, p.A);
                }
            });
        }

        byte[] lut = new byte[ToneLut.Length];
        NegativePrinter.Lut(lut, calibration, print ?? NegativePrintOptions.Default);
        return ApplyToneLut(lut);
    }

    /// <summary>Measures a single-frame image; regions are validated here.</summary>
    internal static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions options, CancellationToken cancellationToken)
    {
        if (image.FrameCount > 1)
        {
            throw new NotSupportedException("A negative must be a single frame.");
        }

        if (options.Region is { } region)
        {
            Image.CheckRectangle(region, image.Width, image.Height, nameof(options));
        }

        if (options.FilmBaseRegion is { } filmBase)
        {
            Image.CheckRectangle(filmBase, image.Width, image.Height, nameof(options));
        }

        ImageFrame frame = image.Frames[0];
        return NegativeCalibrator.Analyze(frame.GetPixelSpan(), image.Width, image.Height, options, cancellationToken);
    }

    private void CheckSingleFrame()
    {
        if (Target.FrameCount > 1)
        {
            throw new NotSupportedException("A negative must be a single frame.");
        }
    }
}
