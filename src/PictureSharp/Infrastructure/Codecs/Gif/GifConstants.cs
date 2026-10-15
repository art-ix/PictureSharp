namespace PictureSharp.Infrastructure.Codecs.Gif;

internal static class GifConstants
{
    public const byte ExtensionIntroducer = 0x21;
    public const byte ImageSeparator = 0x2C;
    public const byte Trailer = 0x3B;

    public const byte GraphicControlLabel = 0xF9;
    public const byte ApplicationLabel = 0xFF;
    public const byte CommentLabel = 0xFE;
    public const byte PlainTextLabel = 0x01;

    /// <summary>LZW codes never exceed 12 bits.</summary>
    public const int MaxCodeBits = 12;
    public const int MaxCodes = 1 << MaxCodeBits;

    /// <summary>The XMP "magic trailer" (bytes 0x01, 0xFF … 0x00) that follows the packet in GIF files.</summary>
    public const int XmpTrailerLength = 257;

    public static ReadOnlySpan<byte> Netscape => "NETSCAPE2.0"u8;
    public static ReadOnlySpan<byte> AnimExts => "ANIMEXTS1.0"u8;
    public static ReadOnlySpan<byte> XmpIdentifier => "XMP DataXMP"u8;
    public static ReadOnlySpan<byte> IccIdentifier => "ICCRGBG1012"u8;
}

/// <summary>GIF frame disposal methods (values 4–7 are reserved and treated as <see cref="None"/>).</summary>
internal enum GifDisposal
{
    /// <summary>Unspecified or "do not dispose": leave the frame in place.</summary>
    None = 0,
    /// <summary>Restore the frame rectangle to the (transparent) background.</summary>
    Background = 2,
    /// <summary>Restore the frame rectangle to the canvas before the frame was drawn.</summary>
    Previous = 3,
}
