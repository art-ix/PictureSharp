using PictureSharp.Application.Abstractions;
using PictureSharp.Application.Decoding;
using PictureSharp.Primitives.Memory;

namespace PictureSharp.Application.Sequences;

/// <summary>Opens frame readers and writers on the codecs of one configuration.</summary>
internal sealed partial class ImageSequenceService(IImageInput input, IImageOutput output, ICodecResolver codecs)
{
    public async Task<ImageFrameReader> OpenReaderAsync(string path, DecoderOptions? options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        Validate(options);
        cancellationToken.ThrowIfCancellationRequested();
        return await OpenReaderAsync(input.OpenRead(path), options, leaveOpen: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImageFrameReader> OpenReaderAsync(Stream stream, DecoderOptions? options, bool leaveOpen, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options = Validate(options);
        var context = new DecodeContext(options);
        var budgeted = new BudgetedReadStream(stream, context, leaveOpen);
        try
        {
            if (!stream.CanRead)
            {
                throw new ArgumentException("The stream must be readable.", nameof(stream));
            }

            cancellationToken.ThrowIfCancellationRequested();
            byte[] header = new byte[codecs.HeaderSize];
            int read = await budgeted.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            IImageDecoder decoder = codecs.GetDecoderFor(header.AsSpan(0, read));
            if (decoder is not ISequentialImageDecoder sequential)
            {
                throw new NotSupportedException($"Reading {decoder.Format.Name} images frame by frame is not supported; use Image.Load.");
            }

            var replay = new PrefixedReadStream(header.AsMemory(0, read), budgeted);
            FrameReadSession session;
            try
            {
                session = await SequenceSessionFactory.OpenReaderAsync(sequential, replay, context, leaveOpen: false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (CodecErrors.IsCorruptDataException(ex))
            {
                throw CodecErrors.CorruptData(decoder.Format, ex);
            }

            return new ImageFrameReader(session);
        }
        catch
        {
            await budgeted.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static DecoderOptions Validate(DecoderOptions? options)
    {
        options ??= DecoderOptions.Default;
        if (options.ColorConversion != ColorConversionMode.None)
            throw new NotSupportedException("Frame readers do not convert colors. Load the image with ColorConversion, or copy the profile and color description from the reader's Metadata to CloneCurrent() before ConvertToSrgb().");
        if (options.TargetSize is not null || options.ApplyExifOrientation)
        {
            throw new NotSupportedException("Frame readers return frames at their stored size; TargetSize and ApplyExifOrientation are not supported.");
        }

        return options;
    }
}
