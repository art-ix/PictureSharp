using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Configuration;
using PictureSharp.Application.Decoding;
using PictureSharp.Application.Encoding;
using PictureSharp.Application.Icons;
using PictureSharp.Application.Sequences;
using PictureSharp.Infrastructure.IO;

namespace PictureSharp.Composition;

/// <summary>Composition root for the dependency-free default library.</summary>
internal static class LibraryServices
{
    private static ImagePorts Ports { get; } = new(new ImageInput(), new ImageOutput());

    /// <summary>The configuration used when none is given; replacing it publishes a complete, immutable snapshot.</summary>
    public static ImageConfiguration Configuration
    {
        get => Volatile.Read(ref field);
        set => Volatile.Write(ref field, value ?? throw new ArgumentNullException(nameof(value)));
    } = new(Ports, CodecRegistry.BuiltIn());

    public static ICodecResolver Codecs => Configuration.Codecs;
    public static ImageReadService Reader => Configuration.Reader;
    public static ImageWriteService Writer => Configuration.Writer;
    public static ImageSequenceService Sequences => Configuration.Sequences;

    /// <summary>The sequence service of <paramref name="options"/>' configuration, or of the default one.</summary>
    public static ImageSequenceService SequencesFor(DecoderOptions? options) => (options?.Configuration ?? Configuration).Sequences;

    /// <summary>The services of <paramref name="options"/>' configuration, or of the default one.</summary>
    public static ImageReadService ReaderFor(DecoderOptions? options) => (options?.Configuration ?? Configuration).Reader;

    public static IconService Icons { get; } = new(CodecRegistry.IcoVariants(), Ports.Input, Ports.Output);
}
