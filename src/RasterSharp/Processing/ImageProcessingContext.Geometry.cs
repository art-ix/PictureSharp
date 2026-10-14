using System.Drawing;
using RasterSharp.Processing;

namespace RasterSharp;

public readonly ref partial struct ImageProcessingContext
{
    /// <summary>Crops to <paramref name="rectangle"/>, which must lie within the image.</summary>
    public ImageProcessingContext Crop(Rectangle rectangle)
    {
        Image.CheckRectangle(rectangle, Target.Width, Target.Height, nameof(rectangle));
        foreach (ImageFrame frame in _state.Frames)
        {
            frame.Replace(rectangle.Width, rectangle.Height, Geometry.Crop(frame, rectangle));
        }

        _state.FramesReplaced();
        return this;
    }

    /// <summary>Crops to the rectangle at (<paramref name="x"/>, <paramref name="y"/>) of the given size, which must lie within the image.</summary>
    public ImageProcessingContext Crop(int x, int y, int width, int height) => Crop(new Rectangle(x, y, width, height));

    /// <summary>Crops to <paramref name="width"/>×<paramref name="height"/>, keeping the part at <paramref name="position"/>.</summary>
    public ImageProcessingContext Crop(int width, int height, AnchorPosition position = AnchorPosition.Center)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, Target.Width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, Target.Height);
        return Crop(new Rectangle(Anchor.Place(Target.Size, new(width, height), position), new(width, height)));
    }

    /// <summary>Rotates clockwise by a multiple of 90°.</summary>
    public ImageProcessingContext Rotate(RotateMode rotateMode) => RotateFlip(rotateMode, FlipMode.None);

    /// <summary>Mirrors the image.</summary>
    public ImageProcessingContext Flip(FlipMode flipMode) => RotateFlip(RotateMode.None, flipMode);

    /// <summary>Rotates clockwise by a multiple of 90°, then mirrors the image.</summary>
    public ImageProcessingContext RotateFlip(RotateMode rotateMode, FlipMode flipMode)
    {
        (int turns, bool flipX) = Geometry.ToQuarterTurns(rotateMode, flipMode);
        return RotateFlip(turns, flipX);
    }

    /// <summary>
    /// Rotates/flips the image according to its EXIF orientation tag and resets the tag to
    /// <see cref="ExifOrientation.TopLeft"/>, so the pixels are stored upright.
    /// </summary>
    public ImageProcessingContext AutoOrient()
    {
        ImageMetadata metadata = Target.Metadata;
        ExifOrientation orientation = metadata.Orientation;
        if (orientation is ExifOrientation.Unknown or ExifOrientation.TopLeft)
        {
            return this;
        }

        (int turns, bool flipX) = Geometry.FromExifOrientation(orientation);
        RotateFlip(turns, flipX);
        metadata.Orientation = ExifOrientation.TopLeft;
        return this;
    }

    /// <summary>
    /// Places the image on a <paramref name="width"/>×<paramref name="height"/> canvas filled with
    /// <paramref name="color"/>, at <paramref name="position"/>, without scaling. Parts outside the canvas are cut off.
    /// </summary>
    public ImageProcessingContext Pad(int width, int height, Rgba32 color = default, AnchorPosition position = AnchorPosition.Center)
    {
        ImageFrame.ValidateSize(width, height);
        Image image = Target;
        (int srcWidth, int srcHeight) = (image.Width, image.Height);
        if (width == srcWidth && height == srcHeight)
        {
            return this;
        }

        Point offset = Anchor.Place(new(width, height), image.Size, position);
        foreach (ImageFrame frame in _state.Frames)
        {
            var canvas = GC.AllocateUninitializedArray<Rgba32>(width * height);
            canvas.AsSpan().Fill(color);
            Geometry.Place(frame.Pixels, srcWidth, srcHeight, canvas, width, height, offset.X, offset.Y);
            frame.Replace(width, height, canvas);
        }

        _state.FramesReplaced();
        if (color.A != 255 && (width > srcWidth || height > srcHeight || offset.X > 0 || offset.Y > 0))
        {
            image.HasAlpha = true;
        }

        return this;
    }

    private ImageProcessingContext RotateFlip(int turns, bool flipX)
    {
        if (turns == 0 && !flipX)
        {
            return this;
        }

        bool newBuffers = (turns & 1) != 0;
        foreach (ImageFrame frame in newBuffers ? _state.Frames : _state.WritableFrames)
        {
            Geometry.RotateFlip(frame, turns, flipX);
        }

        if (newBuffers)
        {
            _state.FramesReplaced();
            ImageMetadata metadata = Target.Metadata;
            (metadata.HorizontalResolution, metadata.VerticalResolution) = (metadata.VerticalResolution, metadata.HorizontalResolution);
        }

        return this;
    }

    /// <summary>
    /// Rotates clockwise by <paramref name="degrees"/>, as Pillow's <c>Image.rotate(-degrees)</c>. With
    /// <paramref name="expand"/> the canvas grows to hold the whole image, otherwise the corners are cut off;
    /// uncovered areas are filled with <paramref name="background"/>. Multiples of 90° are exact.
    /// </summary>
    /// <param name="degrees">Clockwise angle.</param>
    /// <param name="filter"><see cref="ResamplingFilter.Nearest"/>, <see cref="ResamplingFilter.Bilinear"/> or <see cref="ResamplingFilter.Bicubic"/>.</param>
    /// <param name="background">Fill for the uncovered corners. Default: transparent black.</param>
    /// <param name="expand">Whether the canvas grows to fit the rotated image. Default: true.</param>
    public ImageProcessingContext Rotate(float degrees, ResamplingFilter filter = ResamplingFilter.Bicubic, Rgba32 background = default, bool expand = true)
    {
        CheckFinite(degrees, nameof(degrees));
        if (filter is not (ResamplingFilter.Nearest or ResamplingFilter.Bilinear or ResamplingFilter.Bicubic))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, "Rotation supports the Nearest, Bilinear and Bicubic filters.");
        }

        // Pillow rotates counter-clockwise; angle % 360.0 follows Python's sign rules.
        double angle = -(double)degrees % 360.0;
        if (angle < 0)
        {
            angle += 360.0;
        }

        Image image = Target;
        (int w, int h) = (image.Width, image.Height);
        if (angle == 0)
        {
            return this;
        }

        if (angle == 180)
        {
            return RotateFlip(2, false);
        }

        if (angle is 90 or 270 && (expand || w == h))
        {
            return RotateFlip(angle == 90 ? 3 : 1, false);
        }

        (double[] matrix, int width, int height) = Affine.RotationMatrix(w, h, angle, expand);
        ImageFrame.ValidateSize(width, height);
        bool premultiplied = filter != ResamplingFilter.Nearest;
        Rgba32 fill = background;
        if (premultiplied)
        {
            Span<Rgba32> one = [background];
            Premultiplied.FromStraight(one, one);
            fill = one[0];
        }

        Rgba32[]? scratch = premultiplied ? GC.AllocateUninitializedArray<Rgba32>(w * h) : null;
        foreach (ImageFrame frame in _state.Frames)
        {
            var output = GC.AllocateUninitializedArray<Rgba32>(width * height);
            output.AsSpan().Fill(fill);
            if (scratch is null)
            {
                Affine.TransformNearest(frame.Pixels, w, h, output, width, height, matrix);
            }
            else
            {
                Premultiplied.FromStraight(frame.Pixels, scratch);
                Affine.TransformFiltered(scratch, w, h, output, width, height, matrix, filter);
                Premultiplied.ToStraight(output);
            }

            frame.Replace(width, height, output);
        }

        _state.FramesReplaced();
        image.HasAlpha |= background.A != 255;
        return this;
    }
}
