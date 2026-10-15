
namespace PictureSharp.Application.Abstractions;

internal static class InputLimits
{
    /// <summary>Checks an in-memory input against the limit.</summary>
    public static void CheckLength(long length, long maxBytes)
    {
        if (length > maxBytes)
        {
            throw new ImageFormatException(
                $"The input is larger than the limit of {maxBytes} bytes (DecoderOptions.MaxInputBytes).");
        }
    }
}
