using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Configuration;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Encoding;
using PictureSharp.Application.Identification;
using PictureSharp.Application.Sequences;
using PictureSharp.Application.Metadata;

namespace PictureSharp;

/// <summary>
/// The image formats available to loading, identification and saving. A configuration cannot change once built, so
/// one instance can be shared by any number of threads. Derive a new one with <see cref="ToBuilder"/>.
/// </summary>
/// <remarks>
/// <c>ImageConfiguration.Default</c> is used whenever no configuration is given; an application may replace it once at
/// startup. <see cref="DecoderOptions.Configuration"/> selects another configuration for a single load, e.g. to accept
/// only some formats from untrusted uploads.
/// </remarks>
/// <example>
/// <code>
/// var uploads = ImageConfiguration.Default.ToBuilder().KeepOnly(ImageFormat.Png, ImageFormat.Jpeg).Build();
/// using var image = Image.Load(stream, new DecoderOptions { Configuration = uploads });
/// </code>
/// </example>
public sealed class ImageConfiguration
{
    private readonly ImagePorts _ports;
    private readonly FormatRegistration[] _registrations;

    internal ImageConfiguration(ImagePorts ports, FormatRegistration[] registrations)
    {
        _ports = ports;
        _registrations = registrations;
        var codecs = new CodecResolver(registrations);
        Codecs = codecs;
        Reader = new ImageReadService(ports.Input, new ImageDecodeService(codecs), new ImageIdentifyService(codecs), codecs);
        Writer = new ImageWriteService(ports.Output, new ImageEncodeService(codecs), codecs);
        Metadata = new ImageMetadataService(ports.Input, ports.Output, codecs);
        Sequences = new ImageSequenceService(ports.Input, ports.Output, codecs);
    }

    /// <summary>The registered formats in detection order.</summary>
    public IReadOnlyList<ImageFormat> Formats => Codecs.Formats;

    internal ICodecResolver Codecs { get; }

    internal ImageReadService Reader { get; }

    internal ImageWriteService Writer { get; }

    internal ImageSequenceService Sequences { get; }

    internal ImageMetadataService Metadata { get; }

    /// <summary>Whether this configuration can decode the format.</summary>
    public bool CanDecode(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Codecs.GetDecoder(format) is not null;
    }

    /// <summary>Whether this configuration can encode the format.</summary>
    public bool CanEncode(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return Codecs.GetEncoder(format) is not null;
    }

    /// <summary>Finds a registered format by file extension or path, e.g. ".webp" or "photo.JPG".</summary>
    public ImageFormat? FromExtension(string extensionOrPath) => FormatLookup.FromExtension(extensionOrPath, Formats);

    /// <summary>Finds a registered format by MIME type.</summary>
    public ImageFormat? FromMimeType(string mimeType) => FormatLookup.FromMimeType(mimeType, Formats);

    /// <summary>Starts a builder with the formats of this configuration; this configuration is not changed.</summary>
    public ImageConfigurationBuilder ToBuilder() => new(_ports, _registrations);

    /// <inheritdoc/>
    public override string ToString() => $"ImageConfiguration {{ Formats = {string.Join(", ", Formats)} }}";
}
