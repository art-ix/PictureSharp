using System.Drawing;
using PictureSharp.Processing;

namespace PictureSharp;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>
    /// Resizes to <paramref name="width"/>×<paramref name="height"/> with <see cref="ResamplingFilter.Lanczos3"/>.
    /// Pass 0 for one side to keep the aspect ratio, e.g. <c>Resize(800, 0)</c>.
    /// </summary>
    public ImageProcessingContext Resize(int width, int height) =>
        Resize(width, height, ResizeMode.Stretch, ResamplingFilter.Lanczos3);

    /// <summary>Resizes to <paramref name="width"/>×<paramref name="height"/> (0 for one side keeps the aspect ratio) with the given filter.</summary>
    public ImageProcessingContext Resize(int width, int height, ResamplingFilter filter) =>
        Resize(width, height, ResizeMode.Stretch, filter);

    /// <summary>Resizes to <paramref name="size"/> (0 for one side keeps the aspect ratio) with <see cref="ResamplingFilter.Lanczos3"/>.</summary>
    public ImageProcessingContext Resize(Size size) => Resize(size.Width, size.Height);

    /// <summary>
    /// Resizes to <paramref name="width"/>×<paramref name="height"/> (0 for one side keeps the aspect ratio) with the
    /// given mode; <see cref="ResizeMode.Crop"/> keeps the center and <see cref="ResizeMode.Pad"/> pads with transparent black.
    /// </summary>
    public ImageProcessingContext Resize(int width, int height, ResizeMode mode, ResamplingFilter filter = ResamplingFilter.Lanczos3)
    {
        CheckSize(width, height);
        return Resize(new ResizeOptions { Size = new(width, height), Mode = mode, Filter = filter });
    }

    /// <summary>
    /// Shrinks the image to fit inside <paramref name="maxWidth"/>×<paramref name="maxHeight"/> keeping the aspect ratio
    /// (0 for one side means no limit on it). Images that already fit are left unchanged.
    /// </summary>
    public ImageProcessingContext Thumbnail(int maxWidth, int maxHeight, ResamplingFilter filter = ResamplingFilter.Lanczos3)
    {
        CheckSize(maxWidth, maxHeight);
        return Resize(new ResizeOptions { Size = new(maxWidth, maxHeight), Mode = ResizeMode.Max, Filter = filter, AllowUpscale = false });
    }

    /// <summary>
    /// Resizes as described by <paramref name="options"/>. Resampling runs on premultiplied alpha and matches Pillow's
    /// <c>Image.resize</c> pixel for pixel.
    /// </summary>
    public ImageProcessingContext Resize(ResizeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Image image = Target;
        int srcWidth = image.Width;
        int srcHeight = image.Height;
        (int width, int height) = (options.Size.Width, options.Size.Height);
        if (width == 0)
        {
            width = Math.Max(1, (int)Math.Round((double)srcWidth / srcHeight * height));
        }
        else if (height == 0)
        {
            height = Math.Max(1, (int)Math.Round((double)srcHeight / srcWidth * width));
        }

        ImageFrame.ValidateSize(width, height);
        (double cx, double cy) = Anchor.Centering(options.Position);
        (int w, int h) = (width, height);
        (float x0, float y0, float x1, float y1) = (0, 0, srcWidth, srcHeight);
        switch (options.Mode)
        {
            case ResizeMode.Stretch:
                break;
            case ResizeMode.Max:
            case ResizeMode.Pad:
                (w, h) = Resizer.FitSize(srcWidth, srcHeight, width, height);
                break;
            case ResizeMode.Crop:
                (x0, y0, x1, y1) = Resizer.FillBox(srcWidth, srcHeight, width, height, cx, cy);
                break;
            case ResizeMode.Min:
                (w, h) = Resizer.CoverSize(srcWidth, srcHeight, width, height);
                break;
        }

        Rectangle? cropOnly = null;
        if (!options.AllowUpscale && (w > x1 - x0 || h > y1 - y0))
        {
            switch (options.Mode)
            {
                case ResizeMode.Max:
                case ResizeMode.Min:
                    return this;
                case ResizeMode.Pad:
                    (w, h) = (srcWidth, srcHeight);
                    break;
                case ResizeMode.Stretch:
                    (w, h) = (Math.Min(width, srcWidth), Math.Min(height, srcHeight));
                    break;
                case ResizeMode.Crop:
                    (w, h) = (Math.Min(width, srcWidth), Math.Min(height, srcHeight));
                    cropOnly = new Rectangle(Anchor.Place(image.Size, new(w, h), options.Position), new(w, h));
                    break;
            }
        }

        (int canvasWidth, int canvasHeight) = options.Mode == ResizeMode.Pad ? (width, height) : (w, h);
        bool resample = cropOnly is null && (w != srcWidth || h != srcHeight || x0 != 0 || y0 != 0 || x1 != srcWidth || y1 != srcHeight);
        bool pad = canvasWidth != w || canvasHeight != h;
        if (!resample && cropOnly is null && !pad)
        {
            return this;
        }

        Point offset = Anchor.Place(new(canvasWidth, canvasHeight), new(w, h), options.Position);
        foreach (ImageFrame frame in _state.Frames)
        {
            Rgba32[] pixels = cropOnly is { } rect ? Geometry.Crop(frame, rect)
                : resample ? Resizer.Resize(frame.Pixels, srcWidth, srcHeight, w, h, options.Filter, x0, y0, x1, y1)
                : frame.Pixels;
            if (pad)
            {
                var canvas = GC.AllocateUninitializedArray<Rgba32>(canvasWidth * canvasHeight);
                canvas.AsSpan().Fill(options.PadColor);
                Geometry.Place(pixels, w, h, canvas, canvasWidth, canvasHeight, offset.X, offset.Y);
                pixels = canvas;
            }

            frame.Replace(canvasWidth, canvasHeight, pixels);
        }

        _state.FramesReplaced();
        if (pad && options.PadColor.A != 255)
        {
            image.HasAlpha = true;
        }

        return this;
    }

    private static void CheckSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if ((width | height) == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "At least one of width and height must be positive.");
        }
    }
}
