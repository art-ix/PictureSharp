using PictureSharp.Algorithms.Processing;
using PictureSharp.Domain.Frames;

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

        byte[] lut = new byte[ToneLut.Length];
        PhotoAdjust.ExposureLut(lut, stops);
        return ApplyToneLut(lut);
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

        byte[] lut = new byte[ToneLut.Length];
        PhotoAdjust.WhiteBalanceLut(lut, temperature, tint);
        return ApplyToneLut(lut);
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

        foreach (ImageFrame frame in _state.WritableFrames)
        {
            PhotoAdjust.Vibrance(frame.Pixels, frame.Width * frame.Height, amount, _state.CancellationToken);
        }

        return this;
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
            PhotoAdjust.Vignette(frame.Pixels, frame.Width, frame.Height, options, _state.CancellationToken);
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
        return LocalFilter((shadows, highlights, sigma), static (source, destination, w, h, state, token) =>
            LocalTone.ShadowsHighlights(source, destination, w, h, state.shadows, state.highlights, state.sigma, token));
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
        return LocalFilter((amount, sigma), static (source, destination, w, h, state, token) =>
            LocalTone.Clarity(source, destination, w, h, state.amount, state.sigma, token));
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

    private static void CheckUnit(float value, string paramName)
    {
        if (!(value is >= -1f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(paramName, value, "Expected a number between -1 and 1.");
        }
    }
}
