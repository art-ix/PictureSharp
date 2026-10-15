using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

internal static class SequenceSessionFactory
{
    public static async ValueTask<FrameReadSession> OpenReaderAsync(ISequentialImageDecoder codec, Stream input, DecodeContext context,
        bool leaveOpen = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        context.CancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (!input.CanRead) throw new ArgumentException("Input must be readable.", nameof(input));
        if (!input.CanSeek && !codec.SupportsNonSeekableInput)
            throw new NotSupportedException("This sequential decoder requires seekable input.");
        return await codec.OpenAsync(input, context, leaveOpen, cancellationToken).ConfigureAwait(false);
    }

    public static FrameWriteSession OpenWriter(ISequentialImageEncoder codec, Stream output, FrameSequenceInfo info, EncoderOptions options,
        ImageMetadata metadata, DecodeContext context, bool leaveOpen = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        context.CancellationToken.ThrowIfCancellationRequested();
        if (!output.CanWrite) throw new ArgumentException("Output must be writable.", nameof(output));
        if (!output.CanSeek && codec.RequiresSeekableOutput(info))
            throw new NotSupportedException($"Writing {info.Format.Name} frames to this output requires a seekable stream" +
                (info.FrameCount is null ? " or a declared frame count." : "."));
        return codec.Open(output, info, options, metadata, context, leaveOpen);
    }
}
