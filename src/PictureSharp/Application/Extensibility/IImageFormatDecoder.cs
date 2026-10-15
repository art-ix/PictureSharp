namespace PictureSharp.Formats;

/// <summary>
/// Decodes one format for an <see cref="IImageFormatModule"/>. Implementations must be thread-safe and must not keep
/// the input or context after returning.
/// </summary>
/// <remarks>
/// Corrupt input should throw <see cref="ImageFormatException"/>; index, argument, overflow and invalid-operation
/// errors are also reported to the caller as <see cref="ImageFormatException"/>. Valid input using an unsupported
/// feature should throw <see cref="NotSupportedException"/>. The library applies <see cref="DecoderOptions.TargetSize"/>,
/// <see cref="DecoderOptions.ApplyExifOrientation"/> and the pixel and frame limits to the returned image, and sets
/// its <see cref="Image.SourceFormat"/>.
/// </remarks>
public interface IImageFormatDecoder
{
    /// <summary>Reads the dimensions and basic properties without decoding pixels.</summary>
    ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options);

    /// <summary>
    /// Decodes the complete image. Call <see cref="ImageDecodeContext.ReservePixels"/> before allocating pixel buffers
    /// so oversized input fails early.
    /// </summary>
    Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context);
}
