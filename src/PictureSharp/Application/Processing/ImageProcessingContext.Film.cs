using System.Buffers;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;

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
            LocalFilter(new LitPrint(calibration, options.Print, light));
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
            ForEachFrame(new FilmLumaKernel());
        }

        return MapChannels(new NegativePrintMaps(calibration, print ?? NegativePrintOptions.Default));
    }

    // The integer Rec. 709 luma at the image's depth in all three channels.
    private readonly struct FilmLumaKernel : IPixelKernel
    {
        public void Run(Rgba32[] pixels)
        {
            foreach (ref Rgba32 p in pixels.AsSpan())
            {
                byte y = (byte)GrayWorld.Luma(p);
                p = new Rgba32(y, y, y, p.A);
            }
        }

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel>
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba64 p = TPixel.ToRgba64(pixels[i]);
                ushort y = (ushort)GrayWorld.Luma(p);
                pixels[i] = TPixel.FromRgba64(new Rgba64(y, y, y, p.A));
            }
        }
    }

    private readonly struct NegativePrintMaps(NegativeCalibration calibration, NegativePrintOptions print) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => NegativePrinter.Lut(lut, calibration, print);

        public void Fill(Span<ushort> lut) => NegativePrinter.Lut(lut, calibration, print);
    }

    private readonly struct LitPrint(NegativeCalibration calibration, NegativePrintOptions print, FilmFrame.Illumination light) : ILocalFilter
    {
        public void Run<TPixel>(TPixel[] source, TPixel[] destination, int width, int height, CancellationToken cancellationToken)
            where TPixel : unmanaged, IPixelOps<TPixel> =>
            NegativePrinter.PrintLit(source, destination, width, height, calibration, print, light.X, light.Y, light.C, cancellationToken);
    }

    private readonly struct FindFrame(ImageFrame frame, CancellationToken cancellationToken) : IPixelOperation<FilmFrame?>
    {
        public FilmFrame? Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            FilmFrameDetector.Find(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, cancellationToken);
    }

    private readonly struct AnalyzeFrame(ImageFrame frame, NegativeOptions options, FilmFrame? found, CancellationToken cancellationToken)
        : IPixelOperation<NegativeAnalysis>
    {
        public NegativeAnalysis Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel> =>
            NegativeCalibrator.Analyze<TPixel>(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, options, found, cancellationToken);
    }

    // Rotate(angle).Crop(bounds) in one resampling: the 8-bit port, otherwise the same sampling on premultiplied
    // working values, rounded once.
    private readonly struct StraightenFrame(ImageFrame frame, double[] matrix, Rectangle bounds, CancellationToken cancellationToken)
        : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            (int w, int h) = (frame.Width, frame.Height);
            TPixel[] source = frame.Buffer.As<TPixel>();
            if (typeof(TPixel) == typeof(Rgba32))
            {
                Rgba32[] scratch = GC.AllocateUninitializedArray<Rgba32>(w * h);
                Premultiplied.FromStraight(Unsafe.As<Rgba32[]>(source), scratch);
                Rgba32[] output = new Rgba32[bounds.Width * bounds.Height];
                Affine.TransformFiltered(scratch, w, h, output, bounds.Width, bounds.Height, matrix, ResamplingFilter.Bicubic, cancellationToken,
                    bounds.X, bounds.Y);
                Premultiplied.ToStraight(output);
                return output;
            }

            TPixel[] result = GC.AllocateUninitializedArray<TPixel>(bounds.Width * bounds.Height);
            Vector4[] premultiplied = ArrayPool<Vector4>.Shared.Rent(source.Length);
            Vector4[] transformed = ArrayPool<Vector4>.Shared.Rent(result.Length);
            try
            {
                UnitRows.Premultiply<TPixel, EncodedUnit>(source, premultiplied);
                transformed.AsSpan(0, result.Length).Clear();
                Affine.TransformFilteredWide(premultiplied, w, h, transformed, bounds.Width, bounds.Height, matrix, ResamplingFilter.Bicubic,
                    cancellationToken, bounds.X, bounds.Y);
                UnitRows.Unpremultiply<TPixel, EncodedUnit>(transformed.AsSpan(0, result.Length), result);
            }
            finally
            {
                ArrayPool<Vector4>.Shared.Return(premultiplied);
                ArrayPool<Vector4>.Shared.Return(transformed);
            }

            return PixelBuffer.From(result);
        }
    }

    /// <summary>Measures a single-frame image; regions are validated here.</summary>
    internal static NegativeAnalysis AnalyzeNegative(Image image, NegativeOptions options, CancellationToken cancellationToken) =>
        AnalyzeNegative(image, options, FrameFor(image, options, cancellationToken), cancellationToken);

    /// <summary>Finds the frame of a single-frame image.</summary>
    internal static FilmFrame? FindFilmFrame(Image image, CancellationToken cancellationToken)
    {
        CheckSingleFrame(image);
        var operation = new FindFrame(image.Frames[0], cancellationToken);
        return PixelDispatch.Run<FindFrame, FilmFrame?>(image.PixelType, ref operation);
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

        var operation = new AnalyzeFrame(image.Frames[0], options, frame, cancellationToken);
        return PixelDispatch.Run<AnalyzeFrame, NegativeAnalysis>(image.PixelType, ref operation);
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
            _state.ReplaceFrames((m, bounds), static (source, state, token) =>
            {
                var operation = new StraightenFrame(source, state.m, state.bounds, token);
                return new FrameReplacement(state.bounds.Width, state.bounds.Height,
                    PixelDispatch.Run<StraightenFrame, PixelBuffer>(source.PixelType, ref operation));
            });
            Target.Metadata.PixelsMoved();
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
