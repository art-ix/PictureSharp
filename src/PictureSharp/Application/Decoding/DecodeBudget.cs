
namespace PictureSharp.Application.Decoding;

/// <summary>Monotonic totals for one operation, including nested codecs and streamed frames.</summary>
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

    public void ReserveImage(ImageFormat format, long width, long height, int frames = 1)
    {
        limits.ValidateDimensions(format, width, height);
        if (frames < 1 || frames > limits.MaxFrames - _frames)
            throw new ImageFormatException(format, "Decoded frames exceed DecoderOptions.MaxFrames.");
        // Division avoids overflow even for caller-supplied long.MaxValue limits.
        long available = limits.MaxPixels - _pixels;
        if (width > available / height / frames)
            throw new ImageFormatException(format, "Decoded pixels exceed DecoderOptions.MaxPixels.");
        _pixels += width * height * frames;
        _frames += frames;
    }

    /// <summary>Reserves pixels that are not a frame (a gain map); false when they would exceed the limit.</summary>
    public bool TryReservePixels(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > (limits.MaxPixels - _pixels) / height)
            return false;
        _pixels += width * height;
        return true;
    }

    public void ReserveMetadata(ImageFormat format, long bytes)
    {
        if (bytes < 0 || bytes > RemainingMetadata)
            throw limits.MetadataTooLarge(format);
        _metadata += bytes;
    }
}
