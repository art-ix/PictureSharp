using System.Buffers;
using System.Drawing;
using PictureSharp.Formats.WebP.Container;
using static PictureSharp.Formats.WebP.WebPConstants;

namespace PictureSharp.Formats.WebP.Animation;

internal sealed record EncodedAnimationFrame(Rectangle Rectangle, WebPEncodedImage Image, bool Blend, int Duration)
{
    public bool DisposeBackground { get; set; }
    public bool DisposePrevious { get; init; }
}

internal static class AnimationEncoder
{
    public static void Validate(Image image, WebPEncoderOptions options)
    {
        WebPEncoder.ValidateDimensions(image.Width, image.Height, options.Lossless || options.AllowMixed);
        if (image.LoopCount is < 0 or >= MaxLoopCount)
            throw new ArgumentOutOfRangeException(nameof(image.LoopCount), "WebP loop count must be 0–65535.");
        foreach (ImageFrame frame in image.Frames) _ = Duration(frame.Duration);
    }

    // Candidates are copied into one reusable subframe; the encoders return independent byte arrays.
    public static List<EncodedAnimationFrame> Encode(Image image, WebPEncoderOptions options)
    {
        Image? scratch = null;
        try { return Encode(image, options, ref scratch); }
        finally { scratch?.Dispose(); }
    }

    // port: src/mux/anim_encode.c CacheFrame, KeyFramePenalty
    private static List<EncodedAnimationFrame> Encode(Image image, WebPEncoderOptions options, ref Image? scratch)
    {
        var result = new List<EncodedAnimationFrame>(image.FrameCount);
        (int minimum, int maximum) = KeyframeBounds(options);
        int lastKeyframe = 0, bestIndex = -1;
        long bestPenalty = long.MaxValue;
        EncodedAnimationFrame? bestKeyframe = null;
        for (int i = 0; i < image.FrameCount; i++)
        {
            bool forceKeyframe = i == 0 || maximum == 1;
            EncodedAnimationFrame frame = SetFrame(image, i, result, options, forceKeyframe, ref scratch);
            result.Add(frame);
            if (forceKeyframe || maximum == int.MaxValue) continue;
            int distance = i - lastKeyframe;
            if (distance > minimum)
            {
                EncodedAnimationFrame keyframe = SetFrame(image, i, result, options, true, ref scratch);
                long penalty = keyframe.Image.DiskSize - frame.Image.DiskSize;
                if (penalty <= bestPenalty)
                {
                    bestPenalty = penalty; bestIndex = i; bestKeyframe = keyframe;
                }
            }
            if (distance < maximum) continue;

            result[bestIndex] = bestKeyframe!;
            result[bestIndex - 1].DisposeBackground = false;
            // A selected keyframe changes its disposal rectangle; rebuild the buffered suffix.
            for (int j = bestIndex + 1; j <= i; j++) result[j] = SetFrame(image, j, result, options, false, ref scratch);
            lastKeyframe = bestIndex;
            bestPenalty = long.MaxValue; bestIndex = -1; bestKeyframe = null;
        }
        return result;
    }

    // port: src/mux/anim_encode.c SanitizeEncoderOptions, DisableKeyframes
    private static (int Minimum, int Maximum) KeyframeBounds(WebPEncoderOptions options)
    {
        if (options.MinimizeSize || options.KeyframeMax == 0) return (int.MaxValue - 1, int.MaxValue);
        if (options.KeyframeMax == 1) return (0, 1);
        int maximum = options.KeyframeMax;
        int minimum = Math.Min(options.KeyframeMin, maximum - 1);
        int limit = maximum / 2 + 1;
        if (minimum < limit && limit < maximum) minimum = limit;
        minimum = Math.Max(minimum, maximum - 30);
        return (minimum, maximum);
    }

    private static int Duration(TimeSpan duration)
    {
        long milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (duration.Ticks % TimeSpan.TicksPerMillisecond >= TimeSpan.TicksPerMillisecond / 2) milliseconds++;
        if (duration.Ticks < 0 || milliseconds >= MaxDuration)
            throw new ArgumentOutOfRangeException(nameof(duration), "WebP frame duration must round to 0–16777215 milliseconds.");
        return (int)milliseconds;
    }

    // port: src/mux/anim_encode.c SetFrame, GenerateCandidates, PickBestCandidate
    private static EncodedAnimationFrame SetFrame(Image image, int index, List<EncodedAnimationFrame> frames,
        WebPEncoderOptions options, bool keyframe, ref Image? scratch)
    {
        int width = image.Width, height = image.Height;
        ReadOnlySpan<Rgba32> current = image.Frames[index].GetPixelSpan();
        Rgba32[]? disposed = null;
        EncodedAnimationFrame? best = null;
        try
        {
            if (index == 0)
            {
                disposed = ArrayPool<Rgba32>.Shared.Rent(width * height);
                disposed.AsSpan(0, width * height).Clear();
                GenerateCandidates(disposed.AsSpan(0, width * height), current, width, height, false, false, false,
                    Duration(image.Frames[index].Duration), options, ref best, ref scratch);
            }
            else
            {
                ReadOnlySpan<Rgba32> previous = image.Frames[index - 1].GetPixelSpan();
                int duration = Duration(image.Frames[index].Duration);
                GenerateCandidates(previous, current, width, height, keyframe, false, !keyframe, duration, options, ref best, ref scratch);
                if (!keyframe)
                {
                    disposed = ArrayPool<Rgba32>.Shared.Rent(width * height);
                    previous.CopyTo(disposed);
                    DisposeFrameRectangle(disposed.AsSpan(0, width * height), width, frames[index - 1].Rectangle);
                    GenerateCandidates(disposed.AsSpan(0, width * height), current, width, height, false, true, true,
                        duration, options, ref best, ref scratch);
                }
            }
            if (index > 0 && !keyframe) frames[index - 1].DisposeBackground = best!.DisposePrevious;
            return best!;
        }
        finally { if (disposed is not null) ArrayPool<Rgba32>.Shared.Return(disposed); }
    }

    // port: src/mux/anim_encode.c GetSubRects, GenerateCandidates, EncodeCandidate
    private static void GenerateCandidates(ReadOnlySpan<Rgba32> previous, ReadOnlySpan<Rgba32> current,
        int width, int height, bool keyframe, bool disposePrevious, bool allowBlend, int duration, WebPEncoderOptions options,
        ref EncodedAnimationFrame? best, ref Image? scratch)
    {
        Rectangle full = new(0, 0, width, height);
        for (int mode = 0; mode < (options.AllowMixed ? 2 : 1); mode++)
        {
            bool lossless = mode == 0 ? options.Lossless : !options.Lossless;
            Rectangle rectangle = keyframe ? full : GetSubRect(previous, current, width, height, lossless, options.Quality);
            if (!lossless && (rectangle.Width > 16383 || rectangle.Height > 16383)) continue;
            EncodeRectangle(previous, current, width, rectangle, lossless, allowBlend, disposePrevious, duration, options, ref best, ref scratch);
            if (options.MinimizeSize && rectangle != full && (lossless || (width <= 16383 && height <= 16383)))
                EncodeRectangle(previous, current, width, full, lossless, allowBlend, disposePrevious, duration, options, ref best, ref scratch);
        }
    }

    // port: src/mux/anim_encode.c EncodeCandidate, IncreaseTransparency
    private static void EncodeRectangle(ReadOnlySpan<Rgba32> previous, ReadOnlySpan<Rgba32> current,
        int width, Rectangle rectangle, bool lossless, bool allowBlend, bool disposePrevious, int duration,
        WebPEncoderOptions options, ref EncodedAnimationFrame? best, ref Image? scratch)
    {
        if (scratch is null || scratch.Width != rectangle.Width || scratch.Height != rectangle.Height)
        {
            scratch?.Dispose();
            scratch = new Image([ImageFrame.CreateUninitialized(rectangle.Width, rectangle.Height)]);
        }
        Image subframe = scratch;
        for (int y = 0; y < rectangle.Height; y++)
            current.Slice((y + rectangle.Y) * width + rectangle.X, rectangle.Width).CopyTo(subframe.GetRowSpan(y));
        WebPEncodedImage encoded = WebPEncoder.EncodeFrame(subframe, options.ForAnimationFrame(lossless, false));
        PickBest(encoded, rectangle, false, disposePrevious, duration, ref best);
        if (!allowBlend || !IsBlendingPossible(previous, current, width, rectangle)) return;
        bool changed = false;
        for (int y = 0; y < rectangle.Height; y++)
        {
            Span<Rgba32> row = subframe.GetRowSpan(y);
            for (int x = 0; x < rectangle.Width; x++)
            {
                if (row[x] == previous[(y + rectangle.Y) * width + rectangle.X + x] && row[x] != default)
                {
                    row[x] = default;
                    changed = true;
                }
            }
        }
        if (!changed) return;
        encoded = WebPEncoder.EncodeFrame(subframe, options.ForAnimationFrame(lossless, true));
        PickBest(encoded, rectangle, true, disposePrevious, duration, ref best);
    }

    // port: src/mux/anim_encode.c PickBestCandidate
    private static void PickBest(WebPEncodedImage encoded, Rectangle rectangle, bool blend, bool disposePrevious,
        int duration, ref EncodedAnimationFrame? best)
    {
        if (best is null || encoded.DiskSize < best.Image.DiskSize)
            best = new EncodedAnimationFrame(rectangle, encoded, blend, duration) { DisposePrevious = disposePrevious };
    }

    // port: src/mux/anim_encode.c GetSubRect, MinimizeChangeRectangle, SnapToEvenOffsets
    internal static Rectangle GetSubRect(ReadOnlySpan<Rgba32> previous, ReadOnlySpan<Rgba32> current,
        int width, int height, bool lossless, float quality)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        int difference = lossless ? 0 : QualityToMaxDiff(quality);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = y * width + x;
            if (lossless ? previous[offset] == current[offset] : PixelsAreSimilar(previous[offset], current[offset], difference)) continue;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) return new Rectangle(0, 0, 1, 1);
        minX &= ~1; minY &= ~1;
        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    // port: src/mux/anim_encode.c QualityToMaxDiff
    private static int QualityToMaxDiff(float quality)
    {
        double value = Math.Sqrt(quality / 100.0);
        return (int)(31 * (1 - value) + value + 0.5);
    }

    // port: src/mux/anim_encode.c PixelsAreSimilar
    private static bool PixelsAreSimilar(Rgba32 source, Rgba32 destination, int difference) =>
        source.A == destination.A &&
        Math.Abs(source.R - destination.R) * destination.A <= difference * 255 &&
        Math.Abs(source.G - destination.G) * destination.A <= difference * 255 &&
        Math.Abs(source.B - destination.B) * destination.A <= difference * 255;

    // port: src/mux/anim_encode.c IsLosslessBlendingPossible
    private static bool IsBlendingPossible(ReadOnlySpan<Rgba32> previous, ReadOnlySpan<Rgba32> current, int width, Rectangle rectangle)
    {
        for (int y = rectangle.Top; y < rectangle.Bottom; y++)
        for (int x = rectangle.Left; x < rectangle.Right; x++)
        {
            int offset = y * width + x;
            if (current[offset].A != 255 && current[offset] != previous[offset]) return false;
        }
        return true;
    }

    // port: src/mux/anim_encode.c DisposeFrameRectangle, ClearRectangle
    private static void DisposeFrameRectangle(Span<Rgba32> canvas, int width, Rectangle rectangle)
    {
        for (int y = rectangle.Top; y < rectangle.Bottom; y++) canvas.Slice(y * width + rectangle.X, rectangle.Width).Clear();
    }
}
