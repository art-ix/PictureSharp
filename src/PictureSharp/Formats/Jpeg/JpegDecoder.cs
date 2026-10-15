using PictureSharp.Formats.Metadata;
using PictureSharp.Internal;

namespace PictureSharp.Formats.Jpeg;

/// <summary>Decoder for baseline, extended sequential and progressive Huffman-coded JPEG files.</summary>
internal sealed class JpegDecoder : IImageDecoder
{
    public static JpegDecoder Instance { get; } = new();

    public ImageFormat Format => ImageFormat.Jpeg;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        JpegFile file = JpegFile.Read(data.Span, options, headerOnly: true);
        JpegFrame frame = file.Frame!;
        (float dpiX, float dpiY) = file.Resolution();
        return new ImageInfo
        {
            Format = ImageFormat.Jpeg,
            Width = frame.Width,
            Height = frame.Height,
            Orientation = file.Exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(file.Exif),
            HorizontalResolution = dpiX,
            VerticalResolution = dpiY,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken) =>
        Decode(data, options, scale: 0, cancellationToken);

    /// <summary>Decodes at 1/<paramref name="scale"/> size (1, 2, 4 or 8), or at the scale chosen for <see cref="DecoderOptions.TargetSize"/> when 0.</summary>
    internal static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, int scale, CancellationToken cancellationToken = default)
    {
        JpegFile file = JpegFile.Read(data.Span, options, headerOnly: false, cancellationToken, scale);
        JpegFrame frame = file.Frame!;
        Rgba32[] pixels;
        try
        {
            if (frame.Progressive)
            {
                foreach (JpegComponent c in frame.Components)
                {
                    InverseDct(c, options.MaxDegreeOfParallelism, cancellationToken);
                }
            }

            pixels = JpegColorConverter.Convert(frame, file.ColorSpace(), options.MaxDegreeOfParallelism, cancellationToken);
        }
        finally
        {
            file.ReturnBuffers();
        }

        var image = new Image([new ImageFrame(frame.OutputWidth, frame.OutputHeight, pixels)]) { SourceFormat = ImageFormat.Jpeg };
        if (frame.ScaleDenominator > 1)
        {
            image.ReducedDecode = (new System.Drawing.Size(frame.Width, frame.Height), frame.ScaleDenominator);
        }

        (float dpiX, float dpiY) = file.Resolution();
        (image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution) = (dpiX, dpiY);
        if (!options.IgnoreMetadata)
        {
            image.Metadata.Exif = file.Exif;
            image.Metadata.Xmp = file.Xmp;
            image.Metadata.IccProfile = file.IccProfile();
        }

        return image;
    }

    /// <summary>
    /// The largest reduction (8, 4 or 2) whose output still has <see cref="DecoderOptions.TargetReducingGap"/> times the
    /// size <see cref="DecoderOptions.TargetSize"/> asks for on both axes, like Pillow's <c>draft</c> in
    /// <c>Image.thumbnail</c>; 1 decodes at full size.
    /// </summary>
    internal static int SelectScale(JpegFrame frame, byte[]? exif, DecoderOptions options)
    {
        if (options.TargetSize is not { } target)
        {
            return 1;
        }

        ExifOrientation orientation = options.IgnoreMetadata || exif is null ? ExifOrientation.Unknown : ExifReader.GetOrientation(exif);
        if (ImageCodec.TargetSize(frame.Width, frame.Height, target, options.ApplyExifOrientation ? orientation : ExifOrientation.Unknown)
            is not { } size)
        {
            return 1;
        }

        double gap = options.TargetReducingGap;
        foreach (int scale in (ReadOnlySpan<int>)[8, 4, 2])
        {
            if (frame.Width >= scale * gap * size.Width && frame.Height >= scale * gap * size.Height)
            {
                return scale;
            }
        }

        return 1;
    }

    /// <summary>Transforms the coefficients collected by a progressive decode into samples.</summary>
    private static void InverseDct(JpegComponent c, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        if (c.Quant is not { } quant)
        {
            return;   // never appeared in a scan: the plane stays mid-gray, as libjpeg's zeroed multipliers give
        }

        // Blocks are independent; rows of blocks may run in parallel.
        short[] coefficients = c.Coefficients!;
        if (c.HeightInBlocks > 1 && Parallelism.IsWorthwhile(maxDegreeOfParallelism, (long)c.WidthInBlocks * c.HeightInBlocks, 1024))
        {
            Parallel.For(0, c.HeightInBlocks, Parallelism.Options(maxDegreeOfParallelism, cancellationToken), by => TransformRow(c, coefficients, quant, by));
        }
        else
        {
            for (int by = 0; by < c.HeightInBlocks; by++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TransformRow(c, coefficients, quant, by);
            }
        }
    }

    private static void TransformRow(JpegComponent c, short[] coefficients, short[] quant, int by)
    {
        int size = c.BlockSize;
        for (int bx = 0; bx < c.WidthInBlocks; bx++)
        {
            int block = (by * c.BlocksPerLine) + bx;
            JpegIdct.Transform(coefficients.AsSpan(block * 64, 64), quant, c.Plane.AsSpan((by * size * c.PlaneStride) + (bx * size)), c.PlaneStride, size);
        }
    }
}
