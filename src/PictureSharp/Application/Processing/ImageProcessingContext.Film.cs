using System.Drawing;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Frames;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Develops a photographed film negative into a positive: finds the frame in the strip (see
    /// <see cref="ImageAnalysisExtensions.FindFilmFrame(Image)"/>), measures it as
    /// <see cref="ImageAnalysisExtensions.AnalyzeNegative(Image, NegativeOptions?)"/> does (unless
    /// <see cref="NegativeOptions.Calibration"/> is given), straightens the strip and crops it to the frame, inverts it
    /// with <see cref="InvertNegative"/> while evening out the backlight measured on the rebate, removes dust when
    /// <see cref="NegativeOptions.DustSize"/> asks for it, then removes color noise. Without a found frame (for example
    /// a frame that is already cropped) the whole image is measured and developed. Alpha is kept.
    /// </summary>
    /// <remarks>
    /// The channels are inverted in the image's own color space, and its ICC profile or color description stays, so
    /// the positive keeps a wide gamut such as Display P3; call <see cref="ConvertToSrgb"/> afterwards when sRGB is
    /// needed. Converting the negative first would clip its strongly colored film base. The positive keeps the
    /// strip's direction: turn it upright with <see cref="Rotate(RotateMode)"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// image.Mutate(x => x.DevelopNegative());
    /// image.Mutate(x => x.DevelopNegative(new NegativeOptions { Region = frame, FilmBaseRegion = rebate }));
    /// </code>
    /// </example>
    /// <exception cref="NotSupportedException">The image has more than one frame.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A region of <paramref name="options"/> is empty or not inside the image.</exception>
    /// <exception cref="ArgumentException"><see cref="NegativeOptions.Frame"/> was found in an image of another size.</exception>
    public ImageProcessingContext DevelopNegative(NegativeOptions? options = null)
    {
        options ??= NegativeOptions.Default;
        CheckSingleFrame();
        FilmFrame? frame = FrameFor(Target, options, _state.CancellationToken);
        NegativeCalibration calibration = options.Calibration ?? AnalyzeNegative(Target, options, frame, _state.CancellationToken).Calibration;
        // The backlight's change in pixel-edge coordinates of the current image; cropping moves the origin.
        FilmFrame.Illumination? lighting = frame is not null && options.CorrectIllumination && !frame.Lighting.IsFlat ? frame.Lighting : null;
        if (frame is not null && options.Crop)
        {
            lighting = StraightenAndCrop(frame, lighting);
        }

        if (lighting is { } light)
        {
            LocalFilter((calibration, options.Print, light), static (source, destination, w, h, state, token) =>
                NegativePrinter.PrintLit(source, destination, w, h, state.calibration, state.Print, state.light.X, state.light.Y, state.light.C, token));
        }
        else
        {
            InvertNegative(calibration, options.Print);
        }

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
    internal static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions options, CancellationToken cancellationToken) =>
        AnalyzeNegative(image, options, FrameFor(image, options, cancellationToken), cancellationToken);

    /// <summary>Finds the frame of a single-frame image.</summary>
    internal static FilmFrame? FindFilmFrame(Image image, CancellationToken cancellationToken)
    {
        CheckSingleFrame(image);
        return FilmFrameDetector.Find(image.Frames[0].Pixels, image.Width, image.Height, cancellationToken);
    }

    private static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions options, FilmFrame? frame, CancellationToken cancellationToken)
    {
        CheckSingleFrame(image);
        if (options.Region is { } region)
        {
            Image.CheckRectangle(region, image.Width, image.Height, nameof(options));
        }

        if (options.FilmBaseRegion is { } filmBase)
        {
            Image.CheckRectangle(filmBase, image.Width, image.Height, nameof(options));
        }

        ImageFrame pixels = image.Frames[0];
        return NegativeCalibrator.Analyze(pixels.GetPixelSpan(), image.Width, image.Height, options, frame, cancellationToken);
    }

    // The given frame, or a found one unless the options measure fixed regions.
    private static FilmFrame? FrameFor(Image image, NegativeOptions options, CancellationToken cancellationToken)
    {
        CheckSingleFrame(image);
        if (options.Frame is { } given)
        {
            if (given.SourceWidth != image.Width || given.SourceHeight != image.Height)
            {
                throw new ArgumentException("The frame was found in an image of another size.", nameof(options));
            }

            return given;
        }

        return options.Region is null && options.FilmBaseRegion is null ? FindFilmFrame(image, cancellationToken) : null;
    }

    // Rotate(frame.Angle).Crop(frame.Bounds) in one resampling, and the backlight's change moved into the crop.
    private FilmFrame.Illumination? StraightenAndCrop(FilmFrame frame, FilmFrame.Illumination? lighting)
    {
        Rectangle bounds = frame.Bounds;
        double[] matrix = [1, 0, 0, 0, 1, 0];
        if (frame.Angle == 0)
        {
            Crop(bounds);
        }
        else
        {
            (double[] m, _, _) = Affine.RotationMatrix(Target.Width, Target.Height, FilmFrameDetector.PillowAngle(frame.Angle), expand: true);
            matrix = m;
            (int w, int h) = (Target.Width, Target.Height);
            _state.ReplaceFrames((m, w, h, bounds), static (source, state, token) =>
            {
                var (m, w, h, bounds) = state;
                Rgba32[] scratch = GC.AllocateUninitializedArray<Rgba32>(w * h);
                Premultiplied.FromStraight(source.Pixels, scratch);
                Rgba32[] output = new Rgba32[bounds.Width * bounds.Height];
                Affine.TransformFiltered(scratch, w, h, output, bounds.Width, bounds.Height, m, ResamplingFilter.Bicubic, token, bounds.X, bounds.Y);
                Premultiplied.ToStraight(output);
                return new FrameReplacement(bounds.Width, bounds.Height, output);
            });
        }

        if (lighting is not { } l)
        {
            return null;
        }

        // Source point = M · (x + bounds.X, y + bounds.Y): substitute it into X · sx + Y · sy + C.
        Span<float> x = stackalloc float[3], y = stackalloc float[3], c = stackalloc float[3];
        for (int i = 0; i < 3; i++)
        {
            double ax = l.X[i], ay = l.Y[i];
            x[i] = (float)((ax * matrix[0]) + (ay * matrix[3]));
            y[i] = (float)((ax * matrix[1]) + (ay * matrix[4]));
            c[i] = (float)(l.C[i] + (ax * ((matrix[0] * bounds.X) + (matrix[1] * bounds.Y) + matrix[2]))
                + (ay * ((matrix[3] * bounds.X) + (matrix[4] * bounds.Y) + matrix[5])));
        }

        return new FilmFrame.Illumination(new(x[0], x[1], x[2]), new(y[0], y[1], y[2]), new(c[0], c[1], c[2]));
    }

    private static void CheckSingleFrame(Image image)
    {
        if (image.FrameCount > 1)
        {
            throw new NotSupportedException("A negative must be a single frame.");
        }
    }

    private void CheckSingleFrame() => CheckSingleFrame(Target);
}
