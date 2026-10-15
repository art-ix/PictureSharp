using PictureSharp.Application.Decoding;
using PictureSharp.Application.Abstractions;
using PictureSharp.Formats.Ico;
using PictureSharp.Infrastructure.IO;

namespace PictureSharp.Infrastructure.Codecs.Ico;

internal sealed class IcoVariantCodec(IcoDecoder decoder, IcoEncoder encoder) : IIcoCodec
{
    public IReadOnlyList<IcoEntryInfo> Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        InputLimits.CheckLength(data.Length, options.MaxInputBytes);
        return Array.AsReadOnly(IcoDirectory.Parse(data, options).Entries.Select(static e => e.Info).ToArray());
    }

    public IcoImageCollection LoadAll(ReadOnlyMemory<byte> data, DecodeContext context)
    {
        DecoderOptions options = context.Options;
        CancellationToken cancellationToken = context.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        StreamBuffer.CheckLength(data.Length, options.MaxInputBytes);
        IcoDirectory directory = IcoDirectory.Parse(data, options);
        long pixels = 0;
        foreach (IcoDirectory.Entry entry in directory.Entries)
        {
            if (!entry.Info.IsSupported)
            {
                throw new NotSupportedException("The ICO contains an unsupported variant.");
            }

            pixels += (long)entry.Info.Width * entry.Info.Height;
        }

        if (pixels > options.MaxPixels)
        {
            throw IcoDirectory.Error("ICO variants exceed DecoderOptions.MaxPixels.");
        }

        var images = new Image[directory.Entries.Length];
        int loaded = 0;
        try
        {
            foreach (IcoDirectory.Entry entry in directory.Entries)
            {
                Image image = decoder.DecodeEntry(entry, context.Child()).Image;
                images[loaded++] = image;
            }

            return new(images);
        }
        catch
        {
            for (int i = 0; i < loaded; i++)
            {
                images[i].Dispose();
            }

            throw;
        }
    }

    public ImageInfo IdentifySelected(ReadOnlyMemory<byte> data, DecoderOptions options) => decoder.Identify(data, options);
    public void Save(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions options, CancellationToken cancellationToken) => encoder.EncodeImages(images, output, options, cancellationToken);
}
