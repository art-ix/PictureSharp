using System.Buffers;
using System.Runtime.InteropServices;
using PictureSharp.Processing.Quantization;

namespace PictureSharp.Formats.Gif;

/// <summary>The palette of one GIF frame and the palette index of the pixels it draws.</summary>
internal sealed class GifFramePalette
{
    public required Rgba32[] Colors { get; init; }

    /// <summary>Index of the transparent entry, or -1 when the palette has none.</summary>
    public required int TransparentIndex { get; init; }

    /// <summary>Palette index per canvas pixel (transparent pixels use <see cref="TransparentIndex"/>).</summary>
    public required byte[] Indices { get; init; }

    /// <summary>Whether all frames share this palette (written once as the global color table).</summary>
    public required bool IsGlobal { get; init; }
}

/// <summary>
/// Chooses palettes. When all frames together have few enough colors they share one exact palette. Otherwise each
/// animation frame tries an exact local palette holding just the colors it draws (<see cref="BuildLocal"/>); when
/// that is not possible a still image is quantized like Pillow's <c>Image.quantize</c> and an animation gets one
/// quantized palette for all frames (stable colors keep the changed rectangles small). Alpha is reduced to
/// transparent or opaque with the threshold; one entry is reserved for transparency when the image has transparent
/// pixels, or when animation frames can use it for unchanged pixels.
/// </summary>
internal sealed class GifPaletteBuilder
{
    /// <summary>Animations with more opaque pixels are sampled evenly to build the shared palette.</summary>
    private const int MaxSamples = 1 << 24;

    private readonly IReadOnlyList<ImageFrame> _frames;
    private readonly GifEncoderOptions _options;
    private readonly Dictionary<uint, byte>? _exact;
    private readonly PaletteMapper? _mapper;
    private readonly Rgba32[]? _colors;
    private readonly int _transparent = -1;

    public GifPaletteBuilder(IReadOnlyList<ImageFrame> frames, GifEncoderOptions options, bool forceShared = false)
    {
        _frames = frames;
        _options = options;
        int threshold = options.TransparencyThreshold;
        HasTransparency = frames.Any(f => HasTransparentPixels(f.GetPixelSpan(), threshold));
        bool wantTransparent = HasTransparency || (frames.Count > 1 && options.OptimizeFrames);

        var unique = new Dictionary<uint, byte>();
        var colors = new List<Rgba32>();
        bool fits = true;
        foreach (ImageFrame frame in frames)
        {
            if (!CollectColors(frame.GetPixelSpan(), unique, colors, options.ColorCount))
            {
                fits = false;
                break;
            }
        }

        if (fits && (colors.Count + (wantTransparent ? 1 : 0) <= options.ColorCount || !HasTransparency))
        {
            _exact = unique;
            _colors = [.. colors];
            _transparent = wantTransparent && colors.Count < options.ColorCount ? colors.Count : -1;
            return;
        }

        if (frames.Count == 1)
        {
            return;
        }

        if (!forceShared)
        {
            IsLocal = true;
            return;
        }

        List<uint> samples = SampleOpaque(frames, threshold);
        _colors = Quantize(CollectionsMarshal.AsSpan(samples), options.ColorCount - (wantTransparent ? 1 : 0), new byte[samples.Count]);
        _transparent = wantTransparent ? _colors.Length : -1;
        _mapper = new PaletteMapper(_colors);
    }

    /// <summary>Whether any pixel of any frame is below the transparency threshold.</summary>
    public bool HasTransparency { get; }

    /// <summary>Whether frames get exact local palettes from <see cref="BuildLocal"/> instead of <see cref="Build"/>.</summary>
    public bool IsLocal { get; }

    /// <summary>The canvas of a frame in local-palette mode: its pixels with alpha reduced to transparent or opaque.</summary>
    public Rgba32[] Binarize(int frameIndex)
    {
        ReadOnlySpan<Rgba32> pixels = _frames[frameIndex].GetPixelSpan();
        int threshold = _options.TransparencyThreshold;
        var result = new Rgba32[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            result[i] = pixels[i].A < threshold ? default : pixels[i] with { A = 255 };
        }

        return result;
    }

    /// <summary>
    /// An exact palette for the pixels a frame draws inside its rectangle, or null when they need more entries than
    /// allowed. With <paramref name="skipUnchanged"/> pixels equal to <paramref name="before"/> (null: an empty
    /// canvas) are left out and a transparent entry stands in for them; failing that, they are drawn with their own colors.
    /// </summary>
    public GifFramePalette? BuildLocal(Rgba32[] rendered, Rgba32[]? before, int width, (int X0, int Y0, int X1, int Y1) rect, bool skipUnchanged)
    {
        int limit = _options.ColorCount;
        for (int attempt = skipUnchanged ? 0 : 1; attempt < 2; attempt++)
        {
            bool skip = attempt == 0 && before is not null;
            var unique = new Dictionary<uint, byte>();
            var colors = new List<Rgba32>();
            bool skipped = false, fits = true;
            for (int y = rect.Y0; y < rect.Y1 && fits; y++)
            {
                for (int x = rect.X0; x < rect.X1; x++)
                {
                    int p = (y * width) + x;
                    Rgba32 pixel = rendered[p];
                    if (pixel.A == 0 || (skip && pixel == before![p]))
                    {
                        skipped |= pixel.A != 0;
                        continue;
                    }

                    uint key = Key(pixel);
                    if (!unique.ContainsKey(key))
                    {
                        if (colors.Count == limit)
                        {
                            fits = false;
                            break;
                        }

                        unique.Add(key, (byte)colors.Count);
                        colors.Add(pixel);
                    }
                }
            }

            bool needTransparent = HasTransparency || skipped;
            if (fits && colors.Count + (needTransparent ? 1 : 0) <= limit)
            {
                int transparent = needTransparent ? colors.Count : -1;
                var indices = new byte[rendered.Length];
                for (int y = rect.Y0; y < rect.Y1; y++)
                {
                    for (int x = rect.X0; x < rect.X1; x++)
                    {
                        int p = (y * width) + x;
                        indices[p] = rendered[p].A != 0 && unique.TryGetValue(Key(rendered[p]), out byte index) ? index : (byte)Math.Max(transparent, 0);
                    }
                }

                return new GifFramePalette { Colors = WithTransparentEntry([.. colors], transparent), TransparentIndex = transparent, Indices = indices, IsGlobal = false };
            }

            if (HasTransparency)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>The palette of a frame when it is not built locally (shared exact, shared quantized or a quantized still image).</summary>
    public GifFramePalette Build(int frameIndex)
    {
        ImageFrame frame = _frames[frameIndex];
        ReadOnlySpan<Rgba32> pixels = frame.GetPixelSpan();
        int threshold = _options.TransparencyThreshold;
        var indices = new byte[pixels.Length];
        if (_exact is not null)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                indices[i] = pixels[i].A < threshold ? (byte)Math.Max(_transparent, 0) : _exact[Key(pixels[i])];
            }

            return new GifFramePalette { Colors = WithTransparentEntry(_colors!, _transparent), TransparentIndex = _transparent, Indices = indices, IsGlobal = true };
        }

        // Scratch buffers come from the pool: a large still image would otherwise allocate several times its size.
        bool[] maskBuffer = ArrayPool<bool>.Shared.Rent(pixels.Length);
        uint[] opaqueBuffer = ArrayPool<uint>.Shared.Rent(pixels.Length);
        byte[]? opaqueIndexBuffer = null;
        try
        {
            Span<bool> mask = maskBuffer.AsSpan(0, pixels.Length);
            int opaqueCount = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                mask[i] = pixels[i].A < threshold;
                if (!mask[i])
                {
                    opaqueBuffer[opaqueCount++] = Key(pixels[i]) | 0xFF000000;
                }
            }

            bool dither = _options.Dither == GifDither.FloydSteinberg;
            if (_mapper is not null)
            {
                _mapper.Map(pixels, frame.Width, frame.Height, dither, mask, Math.Max(_transparent, 0), indices);
                return new GifFramePalette { Colors = WithTransparentEntry(_colors!, _transparent), TransparentIndex = _transparent, Indices = indices, IsGlobal = true };
            }

            // A still image with too many colors, quantized exactly like Pillow (the transparent entry follows the colors).
            opaqueIndexBuffer = ArrayPool<byte>.Shared.Rent(opaqueCount);
            Span<byte> opaqueIndices = opaqueIndexBuffer.AsSpan(0, opaqueCount);
            Rgba32[] palette = Quantize(opaqueBuffer.AsSpan(0, opaqueCount), _options.ColorCount - (HasTransparency ? 1 : 0), opaqueIndices);
            int transparent = HasTransparency ? palette.Length : -1;
            if (dither)
            {
                new PaletteMapper(palette).Map(pixels, frame.Width, frame.Height, dither: true, mask, Math.Max(transparent, 0), indices);
            }
            else
            {
                for (int i = 0, j = 0; i < pixels.Length; i++)
                {
                    indices[i] = mask[i] ? (byte)transparent : opaqueIndices[j++];
                }
            }

            return new GifFramePalette { Colors = WithTransparentEntry(palette, transparent), TransparentIndex = transparent, Indices = indices, IsGlobal = false };
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(maskBuffer);
            ArrayPool<uint>.Shared.Return(opaqueBuffer);
            if (opaqueIndexBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(opaqueIndexBuffer);
            }
        }
    }

    /// <summary>The canvas as a GIF decoder shows a frame drawn completely with <paramref name="palette"/>.</summary>
    public static Rgba32[] Render(GifFramePalette palette)
    {
        byte[] indices = palette.Indices;
        var rendered = new Rgba32[indices.Length];
        for (int i = 0; i < rendered.Length; i++)
        {
            int index = indices[i];
            rendered[i] = index == palette.TransparentIndex ? default : palette.Colors[index];
        }

        return rendered;
    }

    private Rgba32[] Quantize(ReadOnlySpan<uint> pixels, int colors, Span<byte> indices) =>
        _options.Quantizer == GifQuantizer.Octree
            ? OctreeQuantizer.Quantize(pixels, colors, indices)
            : MedianCutQuantizer.Quantize(pixels, colors, indices);

    /// <summary>The opaque pixels of all frames, or an evenly spaced subset of them for long animations.</summary>
    private static List<uint> SampleOpaque(IReadOnlyList<ImageFrame> frames, int threshold)
    {
        long total = frames.Sum(f => (long)f.GetPixelSpan().Length);
        long step = Math.Max(1, (total + MaxSamples - 1) / MaxSamples);
        var samples = new List<uint>((int)Math.Min(total, MaxSamples));
        long position = 0;
        foreach (ImageFrame frame in frames)
        {
            ReadOnlySpan<Rgba32> pixels = frame.GetPixelSpan();
            for (long i = (step - (position % step)) % step; i < pixels.Length; i += step)
            {
                if (pixels[(int)i].A >= threshold)
                {
                    samples.Add(Key(pixels[(int)i]) | 0xFF000000);
                }
            }

            position += pixels.Length;
        }

        return samples;
    }

    private static Rgba32[] WithTransparentEntry(Rgba32[] colors, int transparentIndex)
    {
        if (transparentIndex < 0)
        {
            return colors;
        }

        var result = new Rgba32[colors.Length + 1];
        colors.CopyTo(result, 0);
        result[transparentIndex] = new Rgba32(0, 0, 0);
        return result;
    }

    /// <summary>Adds the opaque colors in first-occurrence order; returns false as soon as there are more than <paramref name="limit"/>.</summary>
    private bool CollectColors(ReadOnlySpan<Rgba32> pixels, Dictionary<uint, byte> unique, List<Rgba32> colors, int limit)
    {
        int threshold = _options.TransparencyThreshold;
        foreach (Rgba32 pixel in pixels)
        {
            if (pixel.A < threshold)
            {
                continue;
            }

            uint key = Key(pixel);
            if (!unique.ContainsKey(key))
            {
                if (colors.Count == limit)
                {
                    return false;
                }

                unique.Add(key, (byte)colors.Count);
                colors.Add(new Rgba32(pixel.R, pixel.G, pixel.B));
            }
        }

        return true;
    }

    private static bool HasTransparentPixels(ReadOnlySpan<Rgba32> pixels, int threshold)
    {
        foreach (Rgba32 pixel in pixels)
        {
            if (pixel.A < threshold)
            {
                return true;
            }
        }

        return false;
    }

    private static uint Key(Rgba32 pixel) => (uint)(pixel.R | (pixel.G << 8) | (pixel.B << 16));
}
