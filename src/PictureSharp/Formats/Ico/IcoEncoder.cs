using System.Buffers.Binary;
using PictureSharp.Internal;

namespace PictureSharp.Formats.Ico;

internal sealed class IcoEncoder : IImageEncoder<IcoEncoderOptions>
{
    public static IcoEncoder Instance { get; } = new();
    public ImageFormat Format => ImageFormat.Ico;
    public void Encode(Image image, Stream output, IcoEncoderOptions options, CancellationToken cancellationToken) =>
        EncodeImages([image], output, options, cancellationToken);

    internal static void EncodeImages(IReadOnlyList<Image> images, Stream output, IcoEncoderOptions options, CancellationToken cancellationToken)
    {
        if (images.Count is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentException("ICO requires 1–65535 variants.", nameof(images));
        }

        foreach (Image image in images)
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Width is < 1 or > 256 || image.Height is < 1 or > 256)
            {
                throw new ArgumentException("ICO dimensions must be 1–256 pixels. Resize explicitly before saving.", nameof(images));
            }

            _ = image.GetPixelSpan();
        }

        // Buffer payloads in pooled segments; the directory must precede them even on non-seekable outputs.
        using var buffer = new PooledBufferStream();
        int directorySize = 6 + (16 * images.Count);
        byte[] directory = new byte[directorySize];
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(directory.AsSpan(4), (ushort)images.Count);
        for (int i = 0; i < images.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Image image = images[i];
            long start = buffer.Position;
            if (options.Encoding == IcoEncoding.Png)
            {
                image.Save(buffer, new PngEncoderOptions { ColorType = PngColorType.Rgba, SaveAnimation = false, Metadata = options.Metadata },
                    cancellationToken);
            }
            else
            {
                IcoDib.Encode(image, buffer);
            }

            Span<byte> entry = directory.AsSpan(6 + (16 * i), 16);
            entry[0] = (byte)(image.Width == 256 ? 0 : image.Width);
            entry[1] = (byte)(image.Height == 256 ? 0 : image.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], checked((uint)(buffer.Position - start)));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], checked((uint)(directorySize + start)));
        }

        output.Write(directory);
        buffer.WriteTo(output);
    }
}
