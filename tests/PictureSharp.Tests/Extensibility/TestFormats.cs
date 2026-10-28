using System.Text;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Extensibility;

/// <summary>Options of the test PPM plugin; shows how a plugin defines its own encoder options record.</summary>
public sealed record PpmEncoderOptions : EncoderOptions
{
    public override ImageFormat Format => PpmFormat.Format;

    /// <summary>Written as a "# " header comment when set.</summary>
    public string? Comment { get; init; }
}

/// <summary>A minimal binary Netpbm (P6, maxval 255) plugin exercising the public format SPI.</summary>
public sealed class PpmFormat(bool reservePixels = true) : IImageFormatModule
{
    public static ImageFormat Format { get; } = ImageFormat.Create("PPM", "image/x-portable-pixmap", ".ppm", ".PNM");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 3;

    public bool IsSupported(ReadOnlySpan<byte> header) =>
        header.Length >= 3 && header[0] == 'P' && header[1] == '6' && IsSpace(header[2]);

    public IImageFormatDecoder? Decoder { get; set; } = new PpmDecoder(reservePixels);

    public IImageFormatEncoder? Encoder { get; set; } = new PpmEncoder();

    internal static bool IsSpace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private sealed class PpmDecoder(bool reservePixels) : IImageFormatDecoder
    {
        public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
        {
            (int width, int height, _) = ReadHeader(data.Span);
            return new ImageInfo { Format = Format, Width = width, Height = height, Color = TestColor.Rgb8, Compression = ImageCompression.Lossless, ContainerItemCount = 1 };
        }

        public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
        {
            (int width, int height, int offset) = ReadHeader(data.Span);
            if (reservePixels)
            {
                context.ReservePixels(width, height, PixelType.Rgba32);
            }

            // Slicing past the end throws ArgumentOutOfRangeException, which the library reports as corrupt data.
            ReadOnlySpan<byte> rgb = data.Span.Slice(offset, checked(width * height * 3));
            var image = new Image(width, height);
            Span<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
            for (int i = 0; i < pixels.Length; i++)
            {
                context.ThrowIfCancellationRequested();
                pixels[i] = new Rgba32(rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]);
            }

            return image;
        }

        private static (int Width, int Height, int Offset) ReadHeader(ReadOnlySpan<byte> data)
        {
            int position = 2;
            int width = ReadNumber(data, ref position);
            int height = ReadNumber(data, ref position);
            int maxValue = ReadNumber(data, ref position);
            if (maxValue != 255)
            {
                throw new NotSupportedException("Only 8-bit PPM files are supported.");
            }

            return (width, height, position + 1);
        }

        private static int ReadNumber(ReadOnlySpan<byte> data, ref int position)
        {
            while (position < data.Length && (IsSpace(data[position]) || data[position] == '#'))
            {
                if (data[position] == '#')
                {
                    while (position < data.Length && data[position] != '\n') position++;
                }
                else
                {
                    position++;
                }
            }

            int start = position;
            while (position < data.Length && char.IsAsciiDigit((char)data[position])) position++;
            if (position == start || !int.TryParse(data[start..position], out int value) || value <= 0)
            {
                throw new ImageFormatException(Format, "Invalid PPM header.");
            }

            return value;
        }
    }

    private sealed class PpmEncoder : IImageFormatEncoder<PpmEncoderOptions>
    {
        public void Encode(Image image, Stream output, PpmEncoderOptions options, CancellationToken cancellationToken)
        {
            string comment = options.Comment is null ? "" : $"# {options.Comment}\n";
            output.Write(Encoding.ASCII.GetBytes($"P6\n{comment}{image.Width} {image.Height}\n255\n"));
            ReadOnlySpan<Rgba32> pixels = image.GetPixelSpan<Rgba32>();
            byte[] rgb = new byte[pixels.Length * 3];
            for (int i = 0; i < pixels.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (pixels[i].R, pixels[i].G, pixels[i].B);
            }

            output.Write(rgb);
        }
    }
}

/// <summary>A decode-only container: "WRAP" followed by any image the configuration can decode.</summary>
public sealed class WrapFormat : IImageFormatModule, IImageFormatDecoder
{
    public static ImageFormat Format { get; } = ImageFormat.Create("WRAP", "application/x-wrapped-image", ".wrap");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("WRAP"u8);

    public IImageFormatDecoder Decoder => this;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        using Image image = Image.Load(data[4..], options);
        return new ImageInfo { Format = Format, Width = image.Width, Height = image.Height, Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => context.DecodeEmbedded(data[4..]);

    public static byte[] Wrap(ReadOnlySpan<byte> inner) => [.. "WRAP"u8, .. inner];
}

/// <summary>A careless plugin whose signature check indexes past short headers.</summary>
public sealed class NaiveSignatureFormat : IImageFormatModule, IImageFormatDecoder
{
    public static ImageFormat Format { get; } = ImageFormat.Create("NAIVE", "image/x-naive", ".naive");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 8;

    public bool IsSupported(ReadOnlySpan<byte> header) => header[7] == 0x42;

    public IImageFormatDecoder Decoder => this;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) => throw new NotSupportedException();

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context) => throw new NotSupportedException();
}

/// <summary>A configurable module for registration checks.</summary>
public sealed class StubFormat(ImageFormat format, int headerSize = 4, bool decoder = true, IImageFormatEncoder? encoder = null)
    : IImageFormatModule, IImageFormatDecoder
{
    public ImageFormat Format => format;

    public int HeaderSize => headerSize;

    public bool IsSupported(ReadOnlySpan<byte> header) => header is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];

    public IImageFormatDecoder? Decoder => decoder ? this : null;

    public IImageFormatEncoder? Encoder => encoder;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        new() { Format = format, Width = 1, Height = 1, Color = TestColor.Rgb8, Compression = ImageCompression.Lossless };

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        context.ReservePixels(1, 1, PixelType.Rgba32);
        return new(1, 1, new Rgba32(255, 0, 0));
    }
}

/// <summary>The shortcut pattern a plugin package offers for its format.</summary>
public static class PpmImageExtensions
{
    extension(Image image)
    {
        public void SaveAsPpm(string path, PpmEncoderOptions? options = null) => image.Save(path, options ?? new());

        public byte[] ToPpm(PpmEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
