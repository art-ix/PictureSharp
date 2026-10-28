using PictureSharp.Domain.Frames;
using PictureSharp.Domain.Hdr;
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Processing;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>The pixel storage of the image after the operations so far.</summary>
    public PixelType PixelType => _state.Image.PixelType;

    /// <summary>
    /// Converts every frame to <paramref name="target"/> storage. Widening to <see cref="PixelType.Rgba64"/> is exact
    /// (<c>v * 257</c>); reducing to <see cref="PixelType.Rgba32"/> rounds to the nearest 8-bit value, or with
    /// <paramref name="dither"/> applies an ordered dither to R, G and B (alpha is rounded) that depends only on the
    /// pixel position, so samples that are exact 8-bit values stay unchanged. All buffers and the type are published
    /// together; durations, canvas and metadata are kept. Converting to the current type does nothing.
    /// </summary>
    /// <param name="target">The new storage.</param>
    /// <param name="dither">Dither the reduction to 8 bits; ignored when widening.</param>
    public ImageProcessingContext ConvertPixelType(PixelType target, bool dither = false)
    {
        PixelTypes.Check(target, nameof(target));
        Image image = Target;
        if (target == image.PixelType)
        {
            return this;
        }

        // Exact widening keeps the samples the attached gain map describes; reduction changes them.
        GainMapAttachment? gainMap = target == PixelType.Rgba64 ? image.AttachedGainMap : null;
        _state.ReplaceFrames(dither, static (frame, dither, token) =>
        {
            int width = frame.Width, height = frame.Height;
            if (frame.PixelType == PixelType.Rgba32)
            {
                Rgba64[] wide = GC.AllocateUninitializedArray<Rgba64>(width * height);
                PixelDepth.Widen(frame.Pixels, wide);
                return new FrameReplacement(width, height, wide);
            }

            Rgba64[] source = frame.Buffer.Rgba64!;
            Rgba32[] narrow = GC.AllocateUninitializedArray<Rgba32>(width * height);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                ReadOnlySpan<Rgba64> row = source.AsSpan(y * width, width);
                Span<Rgba32> output = narrow.AsSpan(y * width, width);
                if (dither)
                {
                    PixelDepth.NarrowDithered(row, output, y);
                }
                else
                {
                    PixelDepth.Narrow(row, output);
                }
            }

            return new FrameReplacement(width, height, narrow);
        });
        image.AttachedGainMap = gainMap;
        return this;
    }
}
