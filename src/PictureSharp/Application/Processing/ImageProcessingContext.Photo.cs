using System.Runtime.CompilerServices;
using PictureSharp.Algorithms.Processing;
using PictureSharp.Application.Processing;
using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Multiplies linear light by 2^<paramref name="stops"/>, as the exposure control of a raw editor: 1 doubles the
    /// light, −1 halves it, 0 keeps the image. Values that exceed white clip. Alpha is kept.
    /// </summary>
    /// <param name="stops">Exposure change in stops (EV); finite.</param>
    public ImageProcessingContext Exposure(float stops)
    {
        CheckFinite(stops, nameof(stops));
        if (stops == 0f)
        {
            return this;
        }

        return MapChannels(new ExposureMaps(stops));
    }

    /// <summary>
    /// Shifts the white balance by scaling the channels in linear light: red by 2^(0.5 · temperature), blue by
    /// 2^(−0.5 · temperature) and green by 2^(−0.5 · tint), then all three so that gray keeps its luminance. Alpha is kept.
    /// </summary>
    /// <param name="temperature">−1 to 1: positive values warm the image (towards yellow-red), negative cool it (towards blue).</param>
    /// <param name="tint">−1 to 1: positive values shift towards magenta, negative towards green.</param>
    public ImageProcessingContext WhiteBalance(float temperature, float tint = 0f)
    {
        CheckUnit(temperature, nameof(temperature));
        CheckUnit(tint, nameof(tint));
        if (temperature == 0f && tint == 0f)
        {
            return this;
        }

        return MapChannels(new WhiteBalanceMaps(temperature, tint));
    }

    /// <summary>
    /// Balances the colors so that <paramref name="neutral"/>, a color picked from something that should be gray,
    /// becomes gray of the same luminance: each channel is scaled in linear light by Y / C, where C is the neutral's
    /// channel and Y its Rec. 709 luma. The alpha of <paramref name="neutral"/> is ignored; the image's alpha is kept.
    /// </summary>
    /// <param name="neutral">
    /// The color to make gray, at 16-bit precision (an <see cref="Rgba32"/> or <see cref="System.Drawing.Color"/> widens
    /// exactly, so a color measured on a 16-bit image keeps its precision); red, green and blue must all be above 0.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A color channel of <paramref name="neutral"/> is 0.</exception>
    public ImageProcessingContext WhiteBalance(Rgba64 neutral)
    {
        if (neutral.R == 0 || neutral.G == 0 || neutral.B == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(neutral), neutral, "Every color channel of the neutral must be above 0.");
        }

        (float r, float g, float b) = PhotoAdjust.NeutralGains(neutral);
        return MapChannels(new GainMaps(r, g, b));
    }

    /// <summary>
    /// Removes a color cast by assuming that the midtones average to gray: the mean linear color M of the pixels that
    /// are not fully transparent, have no channel at 0–2 or 253–255 and whose luma lies between the 10th and 90th
    /// percentile is measured, and each channel is scaled by (Y / M)^strength, where Y is the luma of M, so that the
    /// mean keeps its luminance. An animation is measured as a whole and every frame gets the same gains. Alpha is kept.
    /// </summary>
    /// <param name="strength">0 to 1: 1 makes the mean gray, smaller values correct partly, 0 keeps the image.</param>
    /// <remarks>A scene that really is dominated by one color (grass, sunset) is shifted towards gray as well.</remarks>
    public ImageProcessingContext AutoWhiteBalance(float strength = 1f)
    {
        if (!(strength is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Expected a number between 0 and 1.");
        }

        if (strength == 0f)
        {
            return this;
        }

        return MapChannels(new GrayWorldMaps(_state, strength));
    }

    /// <summary>
    /// Changes the saturation of muted colors more than of saturated ones: positive values scale the chroma around the
    /// luma by 1 + amount · (1 − s), where s is the saturation (max − min) / max; negative values scale it by
    /// 1 + amount, so −1 gives grayscale. Alpha is kept.
    /// </summary>
    /// <param name="amount">−1 to 1; 0 keeps the image.</param>
    public ImageProcessingContext Vibrance(float amount)
    {
        CheckUnit(amount, nameof(amount));
        if (amount == 0f)
        {
            return this;
        }

        return ForEachFrame(new VibranceKernel(amount, _state.CancellationToken));
    }

    /// <summary>
    /// Darkens (positive <paramref name="amount"/>) or brightens (negative) the edges with the default
    /// <see cref="VignetteOptions"/>; see <see cref="Vignette(VignetteOptions)"/>.
    /// </summary>
    /// <param name="amount">−1 to 1; 0 keeps the image.</param>
    public ImageProcessingContext Vignette(float amount)
    {
        CheckUnit(amount, nameof(amount));
        return Vignette(new VignetteOptions { Amount = amount });
    }

    /// <summary>
    /// Multiplies linear light by 2^(−2 · Amount · w), where w rises smoothly from 0 at
    /// <see cref="VignetteOptions.Midpoint"/> to 1 at the end of the <see cref="VignetteOptions.Feather"/> transition.
    /// The center is kept, as is alpha.
    /// </summary>
    public ImageProcessingContext Vignette(VignetteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Amount == 0f)
        {
            return this;
        }

        foreach (ImageFrame frame in _state.WritableFrames)
        {
            var operation = new VignetteFrame(frame, options, _state.CancellationToken);
            PixelDispatch.Run<VignetteFrame, bool>(frame.PixelType, ref operation);
        }

        return this;
    }

    /// <summary>
    /// Brightens or darkens areas by how light their surroundings are: each pixel's linear light is multiplied by
    /// 2^(1.5 · (shadows · (1 − B)² + highlights · B²)), where B is a Gaussian blur of the luma. Alpha is kept.
    /// </summary>
    /// <param name="shadows">−1 to 1: positive values lift dark areas, negative deepen them.</param>
    /// <param name="highlights">−1 to 1: negative values recover light areas, positive brighten them.</param>
    /// <param name="radius">Standard deviation of the blur in pixels; 0 (the default) uses 2% of the shorter side, at least 1.</param>
    public ImageProcessingContext ShadowsHighlights(float shadows, float highlights, float radius = 0f)
    {
        CheckUnit(shadows, nameof(shadows));
        CheckUnit(highlights, nameof(highlights));
        CheckRadius(radius, nameof(radius));
        if (shadows == 0f && highlights == 0f)
        {
            return this;
        }

        float sigma = radius > 0f ? radius : LocalTone.AutomaticSigma(Target.Width, Target.Height);
        return LocalToneFilter(new LocalToneSettings(shadows, highlights, 0f, sigma));
    }

    /// <summary>
    /// Increases (positive <paramref name="amount"/>) or softens (negative) local contrast: adds
    /// 255 · amount · (P − B) · 4P(1 − P) to each color channel, where P is the luma (0–1) and B its Gaussian blur, so
    /// midtones change most and black and white stay. Alpha is kept.
    /// </summary>
    /// <param name="amount">−1 to 1; 0 keeps the image.</param>
    /// <param name="radius">Standard deviation of the blur in pixels; 0 (the default) uses 2% of the shorter side, at least 1.</param>
    public ImageProcessingContext Clarity(float amount, float radius = 0f)
    {
        CheckUnit(amount, nameof(amount));
        CheckRadius(radius, nameof(radius));
        if (amount == 0f)
        {
            return this;
        }

        float sigma = radius > 0f ? radius : LocalTone.AutomaticSigma(Target.Width, Target.Height);
        return LocalToneFilter(new LocalToneSettings(0f, 0f, amount, sigma));
    }

    /// <summary>
    /// Smooths noise while keeping edges, with a guided filter (He et al.) on each premultiplied color channel: areas
    /// whose variation within the window is small compared with (0.05 · strength)² (values 0–1) are flattened, edges
    /// are kept. Alpha is kept.
    /// </summary>
    /// <param name="strength">0 to 1; 0 keeps the image.</param>
    /// <param name="radius">Window radius in pixels, at least 1; the window is (2 · radius + 1)² pixels.</param>
    public ImageProcessingContext Denoise(float strength, int radius = 2)
    {
        if (!(strength is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "Expected a number between 0 and 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        if (strength == 0f)
        {
            return this;
        }

        return LocalFilter((strength, radius), static (source, destination, w, h, state, token) =>
            GuidedFilter.Denoise(source, destination, w, h, state.strength, state.radius, token));
    }

    /// <summary>Runs shadows/highlights or clarity into new buffers that replace all frames together.</summary>
    private ImageProcessingContext LocalToneFilter(LocalToneSettings settings)
    {
        _state.ReplaceFrames(settings, static (frame, settings, token) =>
        {
            var operation = new LocalToneFrame(frame, settings, token);
            return new FrameReplacement(frame.Width, frame.Height, PixelDispatch.Run<LocalToneFrame, PixelBuffer>(frame.PixelType, ref operation));
        });
        return this;
    }

    /// <summary>Clarity when <see cref="Amount"/> is not 0, otherwise shadows/highlights.</summary>
    private readonly record struct LocalToneSettings(float Shadows, float Highlights, float Amount, float Sigma);

    private readonly struct LocalToneFrame(ImageFrame frame, LocalToneSettings settings, CancellationToken token) : IPixelOperation<PixelBuffer>
    {
        public PixelBuffer Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            (int w, int h) = (frame.Width, frame.Height);
            TPixel[] source = frame.Buffer.As<TPixel>();
            TPixel[] result = GC.AllocateUninitializedArray<TPixel>(w * h);
            if (typeof(TPixel) == typeof(Rgba32))
            {
                Rgba32[] narrowSource = Unsafe.As<Rgba32[]>(source), narrowResult = Unsafe.As<Rgba32[]>(result);
                if (settings.Amount != 0f)
                {
                    LocalTone.Clarity(narrowSource, narrowResult, w, h, settings.Amount, settings.Sigma, token);
                }
                else
                {
                    LocalTone.ShadowsHighlights(narrowSource, narrowResult, w, h, settings.Shadows, settings.Highlights, settings.Sigma, token);
                }
            }
            else if (settings.Amount != 0f)
            {
                LocalTone.ClarityWide(source, result, w, h, settings.Amount, settings.Sigma, token);
            }
            else
            {
                LocalTone.ShadowsHighlightsWide(source, result, w, h, settings.Shadows, settings.Highlights, settings.Sigma, token);
            }

            return PixelBuffer.From(result);
        }
    }

    /// <summary>Runs <paramref name="filter"/> into new buffers that replace all frames together.</summary>
    private ImageProcessingContext LocalFilter<TState>(TState state, LocalFilterAction<TState> filter)
    {
        (int w, int h) = (Target.Width, Target.Height);
        _state.ReplaceFrames((state, filter, w, h), static (frame, data, token) =>
        {
            Rgba32[] result = GC.AllocateUninitializedArray<Rgba32>(data.w * data.h);
            data.filter(frame.Pixels, result, data.w, data.h, data.state, token);
            return new FrameReplacement(data.w, data.h, result);
        });
        return this;
    }

    private delegate void LocalFilterAction<TState>(Rgba32[] source, Rgba32[] destination, int width, int height,
        TState state, CancellationToken cancellationToken);

    private readonly struct ExposureMaps(float stops) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => PhotoAdjust.ExposureLut(lut, stops);

        public void Fill(Span<ushort> lut) => PhotoAdjust.ExposureLut(lut, stops);
    }

    private readonly struct WhiteBalanceMaps(float temperature, float tint) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => PhotoAdjust.WhiteBalanceLut(lut, temperature, tint);

        public void Fill(Span<ushort> lut) => PhotoAdjust.WhiteBalanceLut(lut, temperature, tint);
    }

    private readonly struct GainMaps(float red, float green, float blue) : IChannelMaps
    {
        public void Fill(Span<byte> lut) => PhotoAdjust.ChannelGainsLut(lut, red, green, blue);

        public void Fill(Span<ushort> lut) => PhotoAdjust.ChannelGainsLut(lut, red, green, blue);
    }

    /// <summary>The gray-world gains of <see cref="AutoWhiteBalance"/>, measured on every frame at the image's depth.</summary>
    private readonly struct GrayWorldMaps(ProcessingState state, float strength) : IChannelMaps
    {
        public void Fill(Span<byte> lut)
        {
            CancellationToken token = state.CancellationToken;
            Span<long> luma = stackalloc long[GrayWorld.LumaLength];
            foreach (ImageFrame frame in state.Image.Frames)
            {
                GrayWorld.CountLuma(frame.Buffer.As<Rgba32>(), luma, token);
            }

            if (GrayWorld.MidtoneRange(luma) is not { } range)
            {
                return;
            }

            long[] counts = new long[GrayWorld.ChannelLength];
            foreach (ImageFrame frame in state.Image.Frames)
            {
                GrayWorld.CountChannels(frame.Buffer.As<Rgba32>(), range, counts, token);
            }

            GrayWorld.Lut(lut, counts, strength);
        }

        public void Fill(Span<ushort> lut)
        {
            CancellationToken token = state.CancellationToken;
            long[] luma = new long[GrayWorld.LumaLength16];
            foreach (ImageFrame frame in state.Image.Frames)
            {
                GrayWorld.CountLuma(frame.Buffer.As<Rgba64>(), luma, token);
            }

            if (GrayWorld.MidtoneRange(luma) is not { } range)
            {
                return;
            }

            long[] counts = new long[GrayWorld.ChannelLength16];
            foreach (ImageFrame frame in state.Image.Frames)
            {
                GrayWorld.CountChannels(frame.Buffer.As<Rgba64>(), range, counts, token);
            }

            if (GrayWorld.Gains(counts, GrayWorld.LumaLength16, strength) is { } gains)
            {
                PhotoAdjust.ChannelGainsLut(lut, gains.R, gains.G, gains.B);
            }
        }
    }

    private readonly struct VibranceKernel(float amount, CancellationToken token) : IPixelKernel
    {
        public void Run(Rgba32[] pixels) => PhotoAdjust.Vibrance(pixels, pixels.Length, amount, token);

        public void RunWide<TPixel>(TPixel[] pixels) where TPixel : unmanaged, IPixelOps<TPixel> => PhotoAdjust.VibranceWide(pixels, amount, token);
    }

    private static void CheckUnit(float value, string paramName)
    {
        if (!(value is >= -1f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Expected a number between -1 and 1.");
        }
    }

    private readonly struct VignetteFrame(ImageFrame frame, VignetteOptions options, CancellationToken cancellationToken) : IPixelOperation<bool>
    {
        public bool Run<TPixel>() where TPixel : unmanaged, IPixelOps<TPixel>
        {
            PhotoAdjust.Vignette(frame.Buffer.As<TPixel>(), frame.Width, frame.Height, options, cancellationToken);
            return true;
        }
    }
}
