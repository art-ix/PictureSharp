
using PictureSharp.Domain.Pixels;

namespace PictureSharp.Application.Decoding;

/// <summary>
/// Monotonic totals for one operation, including nested codecs and streamed frames. Pixels are weighted units: a
/// 16-bit (<see cref="PixelType.Rgba64"/>) pixel counts twice against <see cref="DecoderOptions.MaxPixels"/>.
/// </summary>
internal sealed class DecodeBudget(DecoderOptions limits)
{
    private long _pixels;
    private long _input;
    private long _frames;
    private long _metadata;
    public long InputBytes => _input;
    public long RemainingInput => limits.MaxInputBytes - _input;

    public void ReserveInput(long bytes)
    {
        if (bytes < 0 || bytes > RemainingInput) throw new ImageFormatException("Input exceeds DecoderOptions.MaxInputBytes.");
        _input += bytes;
    }

    public long Pixels => _pixels;
    public long Frames => _frames;
    public long MetadataBytes => _metadata;
    public long RemainingMetadata => limits.MaxMetadataBytes - _metadata;

    public void ReserveImage(ImageFormat format, long width, long height, int frames = 1, PixelType pixelType = PixelType.Rgba32)
    {
        limits.ValidateDimensions(format, width, height, pixelType);
        if (frames < 1 || frames > limits.MaxFrames - _frames)
            throw new ImageFormatException(format, "Decoded frames exceed DecoderOptions.MaxFrames.");
        // Division avoids overflow even for caller-supplied long.MaxValue limits.
        int weight = PixelTypes.Weight(pixelType);
        long available = limits.MaxPixels - _pixels;
        if (width > available / height / frames / weight)
            throw new ImageFormatException(format, DecodeLimits.PixelsExceeded("Decoded pixels exceed", pixelType));
        _pixels += width * height * frames * weight;
        _frames += frames;
    }

    /// <summary>Reserves pixels that are not a frame (a gain map) at their weight; false when they would exceed the limit.</summary>
    public bool TryReservePixels(long width, long height, PixelType pixelType = PixelType.Rgba32)
    {
        int weight = PixelTypes.Weight(pixelType);
        if (width <= 0 || height <= 0 || width > (limits.MaxPixels - _pixels) / height / weight)
            return false;
        _pixels += width * height * weight;
        return true;
    }

    public void ReserveMetadata(ImageFormat format, long bytes)
    {
        if (bytes < 0 || bytes > RemainingMetadata)
            throw limits.MetadataTooLarge(format);
        _metadata += bytes;
    }
}
