using PictureSharp.Formats.Png;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngEncoder
{
    private sealed class Layout
    {
        public PngColor Color;
        public int BitDepth = 8;
        public Rgba32[]? Palette;
        public Dictionary<uint, byte>? PaletteIndex;
    }

    /// <summary>Whether any pixel is not opaque, and whether all pixels are gray.</summary>
    internal static (bool HasAlpha, bool IsGray) ScanAlphaAndGray(ReadOnlySpan<Rgba32> pixels)
    {
        (int done, bool hasAlpha, bool isGray) = ScanAlphaAndGraySimd(pixels);
        if (hasAlpha && !isGray)
        {
            return (true, false);
        }

        foreach (Rgba32 p in pixels[done..])
        {
            hasAlpha |= p.A != 255;
            isGray &= p.R == p.G && p.G == p.B;
        }

        return (hasAlpha, isGray);
    }

    private static Layout ChooseLayout(List<ImageFrame> frames, PngColorType requested)
    {
        bool hasAlpha = false;
        bool isGray = true;
        var colors = new Dictionary<uint, byte>();
        bool fitsPalette = true;
        foreach (ImageFrame frame in frames)
        {
            ReadOnlySpan<Rgba32> pixels = frame.GetPixelSpan();
            int i = 0;
            uint last = 0;
            for (; fitsPalette && i < pixels.Length; i++)
            {
                Rgba32 p = pixels[i];
                hasAlpha |= p.A != 255;
                isGray &= p.R == p.G && p.G == p.B;
                if ((i == 0 || p.PackedValue != last) && !colors.ContainsKey(p.PackedValue))
                {
                    if (colors.Count == 256)
                    {
                        fitsPalette = false;
                    }
                    else
                    {
                        colors.Add(p.PackedValue, 0);
                    }
                }

                last = p.PackedValue;
            }

            if (hasAlpha && !isGray)
            {
                continue;
            }

            (bool alpha, bool gray) = ScanAlphaAndGray(pixels[i..]);
            hasAlpha |= alpha;
            isGray &= gray;
        }

        PngColorType type = requested;
        if (type == PngColorType.Auto)
        {
            type = isGray && !hasAlpha ? PngColorType.Grayscale
                : fitsPalette ? PngColorType.Palette
                : isGray ? PngColorType.GrayscaleAlpha
                : hasAlpha ? PngColorType.Rgba : PngColorType.Rgb;
        }

        switch (type)
        {
            case PngColorType.Grayscale:
                return new Layout { Color = PngColor.Gray };
            case PngColorType.GrayscaleAlpha:
                return new Layout { Color = PngColor.GrayAlpha };
            case PngColorType.Rgb:
                return new Layout { Color = PngColor.Rgb };
            case PngColorType.Rgba:
                return new Layout { Color = PngColor.Rgba };
            default:
                {
                    if (!fitsPalette)
                    {
                        throw new InvalidOperationException("The image has more than 256 colors and cannot be saved as an indexed PNG.");
                    }

                    // Transparent entries first so the tRNS chunk stays short; then by frequency-agnostic value order.
                    Rgba32[] palette = colors.Keys.Select(k => new Rgba32 { PackedValue = k })
                        .OrderBy(c => c.A == 255 ? 1 : 0).ThenBy(c => c.PackedValue).ToArray();
                    var index = new Dictionary<uint, byte>(palette.Length);
                    for (int i = 0; i < palette.Length; i++)
                    {
                        index[palette[i].PackedValue] = (byte)i;
                    }

                    int depth = palette.Length <= 2 ? 1 : palette.Length <= 4 ? 2 : palette.Length <= 16 ? 4 : 8;
                    return new Layout { Color = PngColor.Palette, BitDepth = depth, Palette = palette, PaletteIndex = index };
                }
        }
    }
}
