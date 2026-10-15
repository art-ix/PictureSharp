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

    public Image Decode(ReadOnlyMemory<byte> data, DecoderOptions options, CancellationToken cancellationToken)
    {
        JpegFile file = JpegFile.Read(data.Span, options, headerOnly: false, cancellationToken);
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

        var image = new Image([new ImageFrame(frame.Width, frame.Height, pixels)]) { SourceFormat = ImageFormat.Jpeg };
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
        for (int bx = 0; bx < c.WidthInBlocks; bx++)
        {
            int block = (by * c.BlocksPerLine) + bx;
            JpegIdct.Transform(coefficients.AsSpan(block * 64, 64), quant, c.Plane.AsSpan((by * 8 * c.PlaneStride) + (bx * 8)), c.PlaneStride);
        }
    }
}
