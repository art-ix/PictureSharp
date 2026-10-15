namespace PictureSharp.Formats;

/// <summary>
/// A format plugin: recognizes the format and supplies its decoder and/or encoder. Register it with
/// <see cref="ImageConfigurationBuilder.AddFormat"/>; the members are read once, at registration.
/// </summary>
/// <example>
/// <code>
/// ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TgaFormat()).Build();
/// using var image = Image.Load("picture.tga");
/// </code>
/// </example>
public interface IImageFormatModule
{
    /// <summary>The format, usually a static instance created once with <see cref="ImageFormat.Create"/>.</summary>
    ImageFormat Format { get; }

    /// <summary>Number of leading bytes <see cref="IsSupported"/> needs, from 1 to 4096.</summary>
    int HeaderSize { get; }

    /// <summary>
    /// Whether <paramref name="header"/> starts with this format's signature. The span may be shorter than
    /// <see cref="HeaderSize"/> for short inputs; return false then.
    /// </summary>
    bool IsSupported(ReadOnlySpan<byte> header);

    /// <summary>The decoder, or null when the format can only be written.</summary>
    IImageFormatDecoder? Decoder => null;

    /// <summary>The encoder, or null when the format can only be read.</summary>
    IImageFormatEncoder? Encoder => null;
}
