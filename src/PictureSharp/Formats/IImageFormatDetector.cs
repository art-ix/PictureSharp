namespace PictureSharp.Formats;

/// <summary>Recognizes one image format from its leading signature bytes.</summary>
internal interface IImageFormatDetector
{
    ImageFormat Format { get; }

    /// <summary>Number of leading bytes <see cref="IsSupported"/> needs.</summary>
    int HeaderSize { get; }

    bool IsSupported(ReadOnlySpan<byte> header);
}
