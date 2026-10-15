
namespace PictureSharp.Application.Decoding;

internal static class DecodeLimits
{
    extension(DecoderOptions options)
    {
        public void ValidateDimensions(ImageFormat format, long width, long height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ImageFormatException(format, $"Invalid image dimensions {width}x{height}.");
            }

            if (width > options.MaxPixels / height)
            {
                throw new ImageFormatException(format,
                    $"Image dimensions {width}x{height} exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels).");
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
        public void ValidateFrames(ImageFormat format, long width, long height, int frameCount)
        {
            options.ValidateDimensions(format, width, height);
            if (frameCount > 1 && width > options.MaxPixels / height / frameCount)
            {
                throw new ImageFormatException(format,
                    $"{frameCount} frames of {width}x{height} exceed the limit of {options.MaxPixels} pixels (DecoderOptions.MaxPixels).");
            }
        }
    }
}
