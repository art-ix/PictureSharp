namespace PictureSharp.Formats.Pcx;

internal static class PcxConstants
{
    public const int HeaderSize = 128;
    public const byte Manufacturer = 0x0A;
    public const byte EncodingNone = 0;
    public const byte EncodingRle = 1;
    public const byte VgaMarker = 0x0C;
    public const int VgaTail = 769;
    public const int MaxRun = 63;
}
