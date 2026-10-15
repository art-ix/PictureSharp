using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Extensibility;
using PictureSharp.Formats;

namespace PictureSharp.Application.Configuration;

/// <summary>
/// Wires one format to its detector and codec factories. The factories receive the resolver of the configuration
/// being built, so container codecs (BMP with embedded PNG/JPEG) use the codecs of that same configuration.
/// </summary>
internal sealed record FormatRegistration(
    IImageFormatDetector Detector,
    Func<ICodecResolver, IImageDecoder?> CreateDecoder,
    Func<ICodecResolver, IImageEncoder?> CreateEncoder)
{
    /// <summary>Largest signature a plugin may request; format detection reads this many bytes from files and streams.</summary>
    public const int MaxModuleHeaderSize = 4096;

    public IImageMetadataCodec? MetadataCodec { get; init; }

    public ImageFormat Format => Detector.Format;

    /// <summary>Validates a plugin and snapshots its members, so later changes to the module object have no effect.</summary>
    public static FormatRegistration FromModule(IImageFormatModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ImageFormat format = module.Format ?? throw new ArgumentException("The module has no format.", nameof(module));
        int headerSize = module.HeaderSize;
        if (headerSize is < 1 or > MaxModuleHeaderSize)
        {
            throw new ArgumentException($"The {format.Name} module's HeaderSize must be between 1 and {MaxModuleHeaderSize}.", nameof(module));
        }

        IImageFormatDecoder? decoder = module.Decoder;
        IImageFormatEncoder? encoder = module.Encoder;
        if (decoder is null && encoder is null)
        {
            throw new ArgumentException($"The {format.Name} module has neither a decoder nor an encoder.", nameof(module));
        }

        if (encoder is not null && encoder.CreateDefaultOptions()?.Format != format)
        {
            throw new ArgumentException($"The {format.Name} encoder's default options must be for the {format.Name} format.", nameof(module));
        }

        return new(
            new ModuleDetector(module, format, headerSize),
            codecs => decoder is null ? null : new ModuleDecoder(format, decoder, codecs),
            _ => encoder is null ? null : new ModuleEncoder(format, encoder));
    }
}
