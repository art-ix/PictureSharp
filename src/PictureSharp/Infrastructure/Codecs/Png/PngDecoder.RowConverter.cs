using System.Runtime.InteropServices;

namespace PictureSharp.Infrastructure.Codecs.Png;

internal sealed partial class PngDecoder
{
    private sealed class RowConverter
    {
        private readonly PngColor _color;
        private readonly int _depth;
        private readonly Rgba32[] _palette = new Rgba32[256];
        private readonly int _trnsGray = -1;
        private readonly int _trnsR = -1, _trnsG = -1, _trnsB = -1;

        public RowConverter(PngFile png)
        {
            _color = png.Color;
            _depth = png.BitDepth;
            if (_color == PngColor.Palette)
            {
                // Out-of-range indices decode as opaque black.
                _palette.AsSpan().Fill(new Rgba32(0, 0, 0, 255));
                byte[] plte = png.Palette ?? throw Error("Missing PLTE chunk.");
                for (int i = 0; i < plte.Length / 3; i++)
                {
                    _palette[i] = new Rgba32(plte[3 * i], plte[(3 * i) + 1], plte[(3 * i) + 2], 255);
                }

                if (png.Transparency is { } trns)
                {
                    for (int i = 0; i < Math.Min(trns.Length, 256); i++)
                    {
                        _palette[i].A = trns[i];
                    }
                }
            }
            else if (png.Transparency is { } trns)
            {
                if (_color == PngColor.Gray && trns.Length >= 2)
                {
                    _trnsGray = (trns[0] << 8) | trns[1];
                }
                else if (_color == PngColor.Rgb && trns.Length >= 6)
                {
                    _trnsR = (trns[0] << 8) | trns[1];
                    _trnsG = (trns[2] << 8) | trns[3];
                    _trnsB = (trns[4] << 8) | trns[5];
                }
            }
        }

        private static byte Scale16(int v) => (byte)((v + 128) / 257);

        public void Convert(ReadOnlySpan<byte> row, Span<Rgba32> dst)
        {
            int n = dst.Length;
            switch (_color)
            {
                case PngColor.Gray when _depth < 8:
                    {
                        int mask = (1 << _depth) - 1;
                        int scale = 255 / mask;
                        int perByte = 8 / _depth;
                        for (int x = 0; x < n; x++)
                        {
                            int shift = 8 - (_depth * ((x % perByte) + 1));
                            int v = (row[x / perByte] >> shift) & mask;
                            byte g = (byte)(v * scale);
                            dst[x] = new Rgba32(g, g, g, v == _trnsGray ? (byte)0 : (byte)255);
                        }

                        break;
                    }

                case PngColor.Gray when _depth == 8:
                    for (int x = 0; x < n; x++)
                    {
                        byte g = row[x];
                        dst[x] = new Rgba32(g, g, g, g == _trnsGray ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Gray:
                    for (int x = 0; x < n; x++)
                    {
                        int v = (row[2 * x] << 8) | row[(2 * x) + 1];
                        byte g = Scale16(v);
                        dst[x] = new Rgba32(g, g, g, v == _trnsGray ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Rgb when _depth == 8:
                    for (int x = 0, i = 0; x < n; x++, i += 3)
                    {
                        byte r = row[i], g = row[i + 1], b = row[i + 2];
                        bool transparent = r == _trnsR && g == _trnsG && b == _trnsB;
                        dst[x] = new Rgba32(r, g, b, transparent ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Rgb:
                    for (int x = 0, i = 0; x < n; x++, i += 6)
                    {
                        int r = (row[i] << 8) | row[i + 1];
                        int g = (row[i + 2] << 8) | row[i + 3];
                        int b = (row[i + 4] << 8) | row[i + 5];
                        bool transparent = r == _trnsR && g == _trnsG && b == _trnsB;
                        dst[x] = new Rgba32(Scale16(r), Scale16(g), Scale16(b), transparent ? (byte)0 : (byte)255);
                    }

                    break;
                case PngColor.Palette:
                    {
                        if (_depth == 8)
                        {
                            for (int x = 0; x < n; x++)
                            {
                                dst[x] = _palette[row[x]];
                            }
                        }
                        else
                        {
                            int mask = (1 << _depth) - 1;
                            int perByte = 8 / _depth;
                            for (int x = 0; x < n; x++)
                            {
                                int shift = 8 - (_depth * ((x % perByte) + 1));
                                dst[x] = _palette[(row[x / perByte] >> shift) & mask];
                            }
                        }

                        break;
                    }

                case PngColor.GrayAlpha when _depth == 8:
                    for (int x = 0; x < n; x++)
                    {
                        byte g = row[2 * x];
                        dst[x] = new Rgba32(g, g, g, row[(2 * x) + 1]);
                    }

                    break;
                case PngColor.GrayAlpha:
                    for (int x = 0, i = 0; x < n; x++, i += 4)
                    {
                        byte g = Scale16((row[i] << 8) | row[i + 1]);
                        dst[x] = new Rgba32(g, g, g, Scale16((row[i + 2] << 8) | row[i + 3]));
                    }

                    break;
                case PngColor.Rgba when _depth == 8:
                    MemoryMarshal.Cast<byte, Rgba32>(row[..(n * 4)]).CopyTo(dst);
                    break;
                case PngColor.Rgba:
                    for (int x = 0, i = 0; x < n; x++, i += 8)
                    {
                        dst[x] = new Rgba32(
                            Scale16((row[i] << 8) | row[i + 1]),
                            Scale16((row[i + 2] << 8) | row[i + 3]),
                            Scale16((row[i + 4] << 8) | row[i + 5]),
                            Scale16((row[i + 6] << 8) | row[i + 7]));
                    }

                    break;
            }
        }
    }
}
