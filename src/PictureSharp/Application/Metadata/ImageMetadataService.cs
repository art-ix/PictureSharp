using System.Runtime.CompilerServices;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Domain.Metadata;

namespace PictureSharp.Application.Metadata;

internal sealed class ImageMetadataService(IImageInput input, IImageOutput output, ICodecResolver codecs)
{
    [OverloadResolutionPriority(1)]
    public ImageMetadata Read(ReadOnlyMemory<byte> data, DecoderOptions? options, CancellationToken token)
    {
        DecodeContext context = Context(data.Length, options, token);
        var (format, codec) = Codec(data.Span);
        try { return codec.Read(data, context); }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex)) { throw CodecErrors.CorruptData(format, ex); }
    }

    public ImageMetadata Read(ReadOnlySpan<byte> data, DecoderOptions? options, CancellationToken token)
    {
        _ = Context(data.Length, options, token);
        return Read((ReadOnlyMemory<byte>)data.ToArray(), options, token);
    }
    public byte[] Edit(ReadOnlySpan<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        _ = Context(data.Length, options, token);
        return Edit((ReadOnlyMemory<byte>)data.ToArray(), edit, options, token);
    }

    public ImageMetadata Read(string path, DecoderOptions? options, CancellationToken token) =>
        Read(input.ReadFile(path, Limits(options).MaxInputBytes, token), options, token);
    public ImageMetadata Read(Stream stream, DecoderOptions? options, CancellationToken token) =>
        Read(input.ReadAll(stream, Limits(options).MaxInputBytes, token), options, token);
    public async Task<ImageMetadata> ReadAsync(string path, DecoderOptions? options, CancellationToken token) =>
        Read(await input.ReadFileAsync(path, Limits(options).MaxInputBytes, token).ConfigureAwait(false), options, token);
    public async Task<ImageMetadata> ReadAsync(Stream stream, DecoderOptions? options, CancellationToken token) =>
        Read(await input.ReadAllAsync(stream, Limits(options).MaxInputBytes, token).ConfigureAwait(false), options, token);

    [OverloadResolutionPriority(1)]
    public byte[] Edit(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(edit);
        DecodeContext context = Context(data.Length, options, token);
        var (_, codec) = Codec(data.Span);
        ImageMetadata metadata = Read(data, options, token);
        edit(metadata);
        token.ThrowIfCancellationRequested();
        var outputBudget = new DecodeBudget(context.Options);
        ImageFormat format = codecs.Detect(data.Span)!;
        foreach (MetadataKinds kind in new[] { MetadataKinds.Exif, MetadataKinds.Icc, MetadataKinds.Xmp })
            outputBudget.ReserveMetadata(format, metadata.GetBlockLength(kind));
        _ = ExifDocument.Parse(metadata.ExifData, token);
        return codec.Rewrite(data, metadata, context);
    }

    public void Edit(string source, string destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(edit);
        byte[] result = Edit(input.ReadFile(source, Limits(options).MaxInputBytes, token), edit, options, token);
        output.WriteAtomicFile(destination, result, token);
    }
    public async Task EditAsync(string source, string destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(edit);
        byte[] data = await input.ReadFileAsync(source, Limits(options).MaxInputBytes, token).ConfigureAwait(false);
        byte[] result = Edit(data, edit, options, token);
        await output.WriteAtomicFileAsync(destination, result, token).ConfigureAwait(false);
    }
    public void Edit(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        CheckStreams(source, destination);
        byte[] result = Edit(input.ReadAll(source, Limits(options).MaxInputBytes, token), edit, options, token);
        for (int offset = 0; offset < result.Length;)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, result.Length - offset);
            destination.Write(result, offset, length);
            offset += length;
        }
    }
    public async Task EditAsync(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        CheckStreams(source, destination);
        byte[] data = await input.ReadAllAsync(source, Limits(options).MaxInputBytes, token).ConfigureAwait(false);
        byte[] result = Edit(data, edit, options, token);
        for (int offset = 0; offset < result.Length;)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, result.Length - offset);
            await destination.WriteAsync(result.AsMemory(offset, length), token).ConfigureAwait(false);
            offset += length;
        }
    }

    private static void CheckStreams(Stream source, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination)) throw new ArgumentException("Use separate input and output streams.", nameof(destination));
        if (!destination.CanWrite) throw new ArgumentException("Output must be writable.", nameof(destination));
    }

    private (ImageFormat, IImageMetadataCodec) Codec(ReadOnlySpan<byte> data)
    {
        ImageFormat format = codecs.Detect(data) ?? throw new ImageFormatException("Unrecognized image format.");
        return (format, codecs.GetMetadataCodec(format) ?? throw new NotSupportedException($"Metadata-only operations are not supported for {format.Name}."));
    }
    private static DecoderOptions Limits(DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        if (options.IgnoreMetadata || options.FirstFrameOnly || options.ApplyExifOrientation || options.TargetSize is not null)
            throw new NotSupportedException("Metadata-only operations do not accept pixel transforms, FirstFrameOnly or IgnoreMetadata.");
        return options;
    }
    private static DecodeContext Context(int length, DecoderOptions? options, CancellationToken token)
    {
        var context = new DecodeContext(Limits(options), token);
        context.CheckInput(length);
        return context;
    }
}
