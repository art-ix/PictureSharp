using System.Buffers.Binary;
using PictureSharp.Formats;

namespace PictureSharp.Tests.Extensibility;

/// <summary>
/// A tiled container exercising <see cref="ImageDecodeContext.DecodeEmbeddedPart"/>, the way a TIFF plugin decodes
/// JPEG tiles: "TILE", image width and height, tile width and height (big-endian 16 bit), then per tile in row order
/// a big-endian 32-bit length and an embedded image. Edge tiles overhang the image and are cropped.
/// </summary>
/// <param name="charged">Decodes the tiles with <see cref="ImageDecodeContext.DecodeEmbedded"/> instead, charging them.</param>
/// <param name="afterTile">Called after each tile with its index, e.g. to cancel the load.</param>
/// <param name="partOptions">Decodes the tiles with these part settings, through the overload that takes them.</param>
public sealed class TiledContainerFormat(bool charged = false, Action<int>? afterTile = null, EmbeddedPartOptions? partOptions = null)
    : IImageFormatModule, IImageFormatDecoder
{
    public static ImageFormat Format { get; } = ImageFormat.Create("TILE", "application/x-tiled-image", ".tiles");

    ImageFormat IImageFormatModule.Format => Format;

    public int HeaderSize => 4;

    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("TILE"u8);

    public IImageFormatDecoder Decoder => this;

    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        Header header = Read(data.Span);
        return new ImageInfo { Format = Format, Width = header.Width, Height = header.Height, HasAlpha = true };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        Header header = Read(data.Span);
        context.ReservePixels(header.Width, header.Height);
        var image = new Image(header.Width, header.Height);
        int across = (header.Width + header.TileWidth - 1) / header.TileWidth;
        int down = (header.Height + header.TileHeight - 1) / header.TileHeight;
        int offset = 12;
        for (int i = 0; i < across * down; i++)
        {
            // Slicing past the end throws ArgumentOutOfRangeException, which the library reports as corrupt data.
            int length = BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(offset, 4));
            ReadOnlyMemory<byte> tileData = data.Slice(offset + 4, length);
            offset += 4 + length;
            int x0 = i % across * header.TileWidth, y0 = i / across * header.TileHeight;
            int width = Math.Min(header.TileWidth, header.Width - x0), height = Math.Min(header.TileHeight, header.Height - y0);
            using (Image tile = charged ? context.DecodeEmbedded(tileData)
                : partOptions is null ? context.DecodeEmbeddedPart(tileData, header.TileWidth, header.TileHeight)
                : context.DecodeEmbeddedPart(tileData, header.TileWidth, header.TileHeight, partOptions))
            {
                if (tile.Width < width || tile.Height < height)
                {
                    throw new ImageFormatException(Format, "A tile is smaller than its area of the image.");
                }

                for (int y = 0; y < height; y++)
                {
                    tile.GetRowSpan(y)[..width].CopyTo(image.GetRowSpan(y0 + y)[x0..]);
                }
            }

            afterTile?.Invoke(i);
        }

        return image;
    }

    /// <summary>Builds a container from encoded tiles in row order.</summary>
    public static byte[] Create(int width, int height, int tileWidth, int tileHeight, params byte[][] tiles)
    {
        var output = new List<byte>([.. "TILE"u8]);
        foreach (int value in (int[])[width, height, tileWidth, tileHeight])
        {
            output.AddRange([(byte)(value >> 8), (byte)value]);
        }

        foreach (byte[] tile in tiles)
        {
            output.AddRange([(byte)(tile.Length >> 24), (byte)(tile.Length >> 16), (byte)(tile.Length >> 8), (byte)tile.Length, .. tile]);
        }

        return [.. output];
    }

    private static Header Read(ReadOnlySpan<byte> span)
    {
        var header = new Header(BinaryPrimitives.ReadUInt16BigEndian(span[4..]), BinaryPrimitives.ReadUInt16BigEndian(span[6..]),
            BinaryPrimitives.ReadUInt16BigEndian(span[8..]), BinaryPrimitives.ReadUInt16BigEndian(span[10..]));
        return header.Width > 0 && header.Height > 0 && header.TileWidth > 0 && header.TileHeight > 0
            ? header
            : throw new ImageFormatException(Format, "Empty image or tile.");
    }

    private sealed record Header(int Width, int Height, int TileWidth, int TileHeight);
}
