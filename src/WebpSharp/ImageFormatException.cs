namespace WebpSharp;

/// <summary>Thrown when image data is corrupt, truncated or not in a recognized format.</summary>
public class ImageFormatException : FormatException
{
    /// <summary>Creates the exception.</summary>
    public ImageFormatException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with an inner exception.</summary>
    public ImageFormatException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a specific format.</summary>
    public ImageFormatException(ImageFormat? format, string message, Exception? innerException = null)
        : base(format is null ? message : $"{format.Name}: {message}", innerException)
    {
        Format = format;
    }

    /// <summary>The format being decoded, if known.</summary>
    public ImageFormat? Format { get; }
}
