using PictureSharp.Domain.Pixels;

namespace PictureSharp.Application.Decoding;

internal static class DecodeLimits
{
    /// <summary>The message of an exceeded <see cref="DecoderOptions.MaxPixels"/>, noting the weight of 16-bit pixels.</summary>
    public static string PixelsExceeded(string subject, PixelType pixelType) =>
        pixelType == PixelType.Rgba64
            ? $"{subject} DecoderOptions.MaxPixels (16-bit pixels count twice)."
            : $"{subject} DecoderOptions.MaxPixels.";

    extension(DecoderOptions options)
    {
        public void ValidateDimensions(ImageFormat format, long width, long height, PixelType pixelType = PixelType.Rgba32)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ImageFormatException(format, $"Invalid image dimensions {width}x{height}.");
            }

            if (width > options.MaxPixels / height / PixelTypes.Weight(pixelType))
            {
                throw new ImageFormatException(format, pixelType == PixelType.Rgba64
                    ? $"Image dimensions {width}x{height} at 16 bits per channel exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels; 16-bit pixels count twice)."
                    : $"Image dimensions {width}x{height} exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels).");
            }
        }

        public void ValidateFrameCount(ImageFormat format, long frameCount)
        {
            if (frameCount > options.MaxFrames)
            {
                throw new ImageFormatException(format,
                    $"The image has more than {options.MaxFrames} frames (DecoderOptions.MaxFrames).");
            }
        }

        /// <summary>Adds a metadata block to the running total of one image and returns the new total.</summary>
        public long AddMetadata(ImageFormat format, long total, long blockBytes)
        {
            if (total < 0 || blockBytes < 0 || total > options.MaxMetadataBytes || blockBytes > options.MaxMetadataBytes - total)
            {
                throw options.MetadataTooLarge(format);
            }

            return total + blockBytes;
        }

        public ImageFormatException MetadataTooLarge(ImageFormat format) =>
            new(format, $"The image metadata exceeds the limit of {options.MaxMetadataBytes} bytes (DecoderOptions.MaxMetadataBytes).");

        // Every decoded animation frame is a full canvas, however small its encoded region is.
        public void ValidateFrames(ImageFormat format, long width, long height, int frameCount, PixelType pixelType = PixelType.Rgba32)
        {
            options.ValidateDimensions(format, width, height, pixelType);
            if (frameCount > 1 && width > options.MaxPixels / height / frameCount / PixelTypes.Weight(pixelType))
            {
                throw new ImageFormatException(format,
                    $"{frameCount} frames of {width}x{height} exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels"
                    + (pixelType == PixelType.Rgba64 ? "; 16-bit pixels count twice)." : ")."));
            }
        }
    }
}
