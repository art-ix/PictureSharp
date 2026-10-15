using System.Drawing;
using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;

namespace PictureSharp.Application.Sequences;

internal sealed partial class ImageSequenceService
{
    public ImageFrameWriter CreateWriter(string path, Size size, EncoderOptions options, ImageFrameWriterOptions? sequenceOptions)
    {
        ArgumentNullException.ThrowIfNull(path);
        // Validate capabilities before creating/truncating the destination file.
        (IImageEncoder encoder, FrameSequenceInfo info, ImageMetadata metadata) = Prepare(size, options, sequenceOptions);
        Stream stream = output.CreateFile(path);
        try { return OpenWriter(stream, encoder, info, options, metadata, sequenceOptions, path); }
        catch
        {
            try { stream.Dispose(); }
            finally { output.DeleteFile(path); }
            throw;
        }
    }

    public ImageFrameWriter CreateWriter(Stream stream, Size size, EncoderOptions options, ImageFrameWriterOptions? sequenceOptions)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite) throw new ArgumentException("The stream must be writable.", nameof(stream));
        (IImageEncoder encoder, FrameSequenceInfo info, ImageMetadata metadata) = Prepare(size, options, sequenceOptions);
        return OpenWriter(stream, encoder, info, options, metadata, sequenceOptions, path: null);
    }

    public EncoderOptions DefaultOptions(ImageFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return GetEncoder(format).CreateDefaultOptions();
    }

    private IImageEncoder GetEncoder(ImageFormat format) => codecs.GetEncoder(format)
        ?? throw new NotSupportedException($"Encoding {format.Name} is not supported by this configuration.");

    private (IImageEncoder Encoder, FrameSequenceInfo Info, ImageMetadata Metadata) Prepare(Size size, EncoderOptions options,
        ImageFrameWriterOptions? sequenceOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        sequenceOptions ??= ImageFrameWriterOptions.Default;
        var info = new FrameSequenceInfo(options.Format, size.Width, size.Height, sequenceOptions.FrameCount,
            sequenceOptions.LoopCount, sequenceOptions.AnimationBackgroundColor);
        IImageEncoder encoder = GetEncoder(options.Format);
        if (encoder is not ISequentialImageEncoder)
            throw new NotSupportedException($"Writing {info.Format.Name} images frame by frame is not supported.");
        // Open on a sink to validate format-specific options before touching a path.
        using FrameWriteSession validation = ((ISequentialImageEncoder)encoder).Open(Stream.Null, info, options,
            new ImageMetadata(), WriterContext(), leaveOpen: true);
        return (encoder, info, sequenceOptions.Metadata?.Clone() ?? new ImageMetadata());
    }

    private ImageFrameWriter OpenWriter(Stream destination, IImageEncoder encoder, FrameSequenceInfo info, EncoderOptions options,
        ImageMetadata metadata, ImageFrameWriterOptions? sequenceOptions, string? path)
    {
        bool needsSeek = info.FrameCount != 1 && ((ISequentialImageEncoder)encoder).RequiresSeekableOutput(info);
        Stream encoded = destination;
        bool temporary = needsSeek && !destination.CanSeek;
        if (temporary)
        {
            if (sequenceOptions?.UseTemporaryFile != true)
                throw new NotSupportedException($"Writing {info.Format.Name} frames requires seekable output or UseTemporaryFile.");
            encoded = output.CreateTemporaryFile();
        }

        try
        {
            FrameWriteSession session = info.FrameCount == 1
                ? new StillFrameWriteSession(encoded, info, encoder, options, metadata, WriterContext())
                : SequenceSessionFactory.OpenWriter((ISequentialImageEncoder)encoder, encoded, info, options, metadata, WriterContext());
            return new ImageFrameWriter(session, encoded, temporary ? destination : null, output, path, temporary || path is not null);
        }
        catch
        {
            if (temporary) encoded.Dispose();
            throw;
        }
    }

    // Decoder budgets are a session contract; encoding caller-owned frames imposes no default decode limits.
    private static DecodeContext WriterContext() => new(new DecoderOptions { MaxPixels = long.MaxValue, MaxFrames = int.MaxValue });
}
