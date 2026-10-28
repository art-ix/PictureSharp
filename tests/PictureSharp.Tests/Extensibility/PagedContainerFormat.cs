using System.Buffers.Binary;
using System.Text;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Extensibility;

/// <summary>Page selection of the test <see cref="PagedContainerFormat"/>, the way a TIFF or DDS plugin defines its settings.</summary>
public sealed record PagedDecoderOptions : FormatDecoderOptions
{
    /// <summary>Zero-based page; 0 (the first page) when no options are given.</summary>
    public int Page { get; init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Page)); }
}

/// <summary>Settings of some other plugin, which the paged container must ignore.</summary>
public sealed record OtherDecoderOptions : FormatDecoderOptions
{
    public int Page { get; init; }
}

/// <summary>
/// A multi-page container exercising <see cref="DecoderOptions.FormatOptions"/>: "PAGE", a page count byte, then per
/// page width and height (big-endian 16 bit), one RGBA fill color, an XMP length (big-endian 16 bit) and the XMP.
/// </summary>
public sealed class PagedContainerFormat : IImageFormatModule, IImageFormatDecoder, IImageFormatMetadataCodec
{
    public static ImageFormat Format { get; } = ImageFormat.Create("PAGE", "application/x-paged-image", ".pages");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("PAGE"u8);

    public IImageFormatDecoder Decoder => this;

    public IImageFormatMetadataCodec MetadataCodec => this;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        Page[] pages = Read(data);
        Page page = Select(pages, options);
        return new ImageInfo { Format = Format, Width = page.Width, Height = page.Height, Color = TestColor.Rgba8, Compression = ImageCompression.Lossless, ContainerItemCount = pages.Length };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        Page page = Select(Read(data), context.Options);
        context.ReservePixels(page.Width, page.Height, PixelType.Rgba32);
        context.ThrowIfCancellationRequested();
        var image = new Image(page.Width, page.Height, page.Color);
        if (!context.Options.IgnoreMetadata && page.Xmp.Length > 0)
        {
            context.ReserveMetadata(page.Xmp.Length);
            image.Metadata.Xmp = page.Xmp;
        }

        return image;
    }

    public ImageMetadata Read(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        Page page = Select(Read(data), context.Options);
        var metadata = new ImageMetadata();
        if (page.Xmp.Length > 0)
        {
            context.ReserveMetadata(page.Xmp.Length);
            metadata.Xmp = page.Xmp;
        }

        return metadata;
    }

    /// <summary>Builds a container; each page has a fill color and the XMP text "page N".</summary>
    public static byte[] Create(params (int Width, int Height, Rgba32 Color)[] pages)
    {
        var output = new List<byte>([.. "PAGE"u8, (byte)pages.Length]);
        for (int i = 0; i < pages.Length; i++)
        {
            (int width, int height, Rgba32 color) = pages[i];
            byte[] xmp = Encoding.UTF8.GetBytes($"page {i}");
            output.AddRange([(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, color.R, color.G, color.B, color.A]);
            output.AddRange([(byte)(xmp.Length >> 8), (byte)xmp.Length, .. xmp]);
        }

        return [.. output];
    }

    private static Page Select(Page[] pages, DecoderOptions options)
    {
        int index = options.GetFormatOptions<PagedDecoderOptions>()?.Page ?? 0;
        return index < pages.Length ? pages[index] : throw new ImageFormatException(Format, $"Page {index} is outside the {pages.Length} pages of the file.");
    }

    private static Page[] Read(ReadOnlyMemory<byte> data)
    {
        // Slicing past the end throws ArgumentOutOfRangeException, which the library reports as corrupt data.
        ReadOnlySpan<byte> span = data.Span;
        int count = span[4];
        if (count == 0)
        {
            throw new ImageFormatException(Format, "The container has no pages.");
        }

        var pages = new Page[count];
        int offset = 5;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> header = span.Slice(offset, 10);
            int width = BinaryPrimitives.ReadUInt16BigEndian(header);
            int height = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
            int xmpLength = BinaryPrimitives.ReadUInt16BigEndian(header[8..]);
            if (width == 0 || height == 0)
            {
                throw new ImageFormatException(Format, "Empty page.");
            }

            pages[i] = new Page(width, height, new Rgba32(header[4], header[5], header[6], header[7]), span.Slice(offset + 10, xmpLength).ToArray());
            offset += 10 + xmpLength;
        }

        return pages;
    }

    private sealed record Page(int Width, int Height, Rgba32 Color, byte[] Xmp);
}
