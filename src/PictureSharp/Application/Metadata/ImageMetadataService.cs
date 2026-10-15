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
        return Read(data, Codec(data.Span), context);
    }

    public ImageMetadata Read(ReadOnlySpan<byte> data, DecoderOptions? options, CancellationToken token)
    {
        _ = Context(data.Length, options, token); // Check the limit before copying.
        return Read((ReadOnlyMemory<byte>)data.ToArray(), options, token);
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
    public byte[] Edit(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token) =>
        Rewrite(data, edit, options, token) ?? data.ToArray();

    public byte[] Edit(ReadOnlySpan<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        _ = Context(data.Length, options, token); // Check the limit before copying.
        byte[] copy = data.ToArray();
        return Rewrite(copy, edit, options, token) ?? copy;
    }

    public void Edit(string source, string destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        CheckPaths(source, destination, edit);
        byte[] data = input.ReadFile(source, Limits(options).MaxInputBytes, token);
        byte[]? result = Rewrite(data, edit, options, token);
        if (result is not null || !SamePath(source, destination)) output.WriteAtomicFile(destination, result ?? data, token);
    }

    public async Task EditAsync(string source, string destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        CheckPaths(source, destination, edit);
        byte[] data = await input.ReadFileAsync(source, Limits(options).MaxInputBytes, token).ConfigureAwait(false);
        byte[]? result = Rewrite(data, edit, options, token);
        if (result is not null || !SamePath(source, destination))
            await output.WriteAtomicFileAsync(destination, result ?? data, token).ConfigureAwait(false);
    }

    public void Edit(Stream source, Stream destination, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        CheckStreams(source, destination);
        byte[] data = input.ReadAll(source, Limits(options).MaxInputBytes, token);
        byte[] result = Rewrite(data, edit, options, token) ?? data;
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
        byte[] result = Rewrite(data, edit, options, token) ?? data;
        for (int offset = 0; offset < result.Length;)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(65536, result.Length - offset);
            await destination.WriteAsync(result.AsMemory(offset, length), token).ConfigureAwait(false);
            offset += length;
        }
    }

    // Returns null when the edit leaves every block and the resolution unchanged.
    private byte[]? Rewrite(ReadOnlyMemory<byte> data, Action<ImageMetadata> edit, DecoderOptions? options, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(edit);
        DecodeContext context = Context(data.Length, options, token);
        var (format, codec) = Codec(data.Span);
        ImageMetadata original = Read(data, (format, codec), context);
        ImageMetadata metadata = original.Clone();
        edit(metadata);
        token.ThrowIfCancellationRequested();
        if (Same(original, metadata)) return null;
        var outputBudget = new DecodeBudget(context.Options);
        outputBudget.ReserveMetadata(format, metadata.GetBlockLength(MetadataKinds.Exif));
        outputBudget.ReserveMetadata(format, metadata.GetBlockLength(MetadataKinds.Icc));
        outputBudget.ReserveMetadata(format, metadata.GetBlockLength(MetadataKinds.Xmp));
        _ = ExifDocument.Parse(metadata.ExifData, token);
        return codec.Rewrite(data, original, metadata, new DecodeContext(context.Options, token));
    }

    private static ImageMetadata Read(ReadOnlyMemory<byte> data, (ImageFormat Format, IImageMetadataCodec Codec) target, DecodeContext context)
    {
        try { return target.Codec.Read(data, context); }
        catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex)) { throw CodecErrors.CorruptData(target.Format, ex); }
    }

    private static bool Same(ImageMetadata a, ImageMetadata b) =>
        Same(a.ExifData, b.ExifData) && Same(a.XmpData, b.XmpData) && Same(a.IccData, b.IccData)
        && a.HorizontalResolution == b.HorizontalResolution && a.VerticalResolution == b.VerticalResolution;

    private static bool Same(byte[]? a, byte[]? b) => (a is null) == (b is null) && (a is null || a.AsSpan().SequenceEqual(b));

    // Ordinal comparison: a false mismatch only costs an identical write, never a skipped one.
    private static bool SamePath(string source, string destination) =>
        string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.Ordinal);

    private static void CheckPaths(string source, string destination, Action<ImageMetadata> edit)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(edit);
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
