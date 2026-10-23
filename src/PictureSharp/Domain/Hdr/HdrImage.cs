using System.Drawing;
using System.Globalization;
using PictureSharp.Domain.Images;
using PictureSharp.Primitives.Numerics;

namespace PictureSharp;

/// <summary>
/// A high-dynamic-range image in linear light, reconstructed by <c>image.ToHdr()</c> from an SDR base image and its
/// gain map, or created from caller pixels. Pixels are <see cref="Rgba128"/> with straight alpha; 1 is SDR white,
/// components above 1 are highlights and components below 0 may come from gamut conversion. Nothing is clipped. The
/// pixels are read-only, which keeps <see cref="Headroom"/> and <see cref="ColorSpace"/> valid; concurrent reads are
/// safe. The image owns its buffer and does not depend on the lifetime of the source image.
/// </summary>
public sealed class HdrImage : IDisposable
{
    private readonly Rgba128[] _pixels;
    private bool _disposed;

    /// <summary>
    /// Creates an HDR image from a copy of <paramref name="pixels"/> in linear light, for example decoded by a format
    /// plugin from a floating-point file, so it can be tone mapped or turned into an SDR image with a gain map by
    /// <c>hdr.ToGainMapImage()</c>. <see cref="GainMapKind"/> is <see cref="HdrGainMapKinds.None"/>.
    /// </summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="pixels">Exactly <c>width × height</c> pixels, row by row; all channels must be finite.</param>
    /// <param name="colorSpace">The linear-light color space of the pixels.</param>
    /// <param name="headroom">
    /// The headroom as a linear ratio to SDR white, finite and at least 1. Default: null, the largest R, G or B
    /// component, at least 1.
    /// </param>
    /// <param name="metadata">Metadata to copy into <see cref="Metadata"/>; null gives empty metadata.</param>
    /// <exception cref="ArgumentOutOfRangeException">A dimension, the color space or the headroom is invalid.</exception>
    /// <exception cref="ArgumentException">The pixel count does not match or a channel is not finite.</exception>
    public HdrImage(int width, int height, ReadOnlySpan<Rgba128> pixels, HdrColorSpace colorSpace, float? headroom = null,
        ImageMetadata? metadata = null)
    {
        var dimensions = new ImageDimensions(width, height);
        if (pixels.Length != dimensions.PixelCount)
        {
            throw new ArgumentException("The buffer length must match the image dimensions.", nameof(pixels));
        }

        Check.Defined(colorSpace, nameof(colorSpace));
        if (headroom is { } h && !(float.IsFinite(h) && h >= 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(headroom), h, "The headroom must be finite and at least 1.");
        }

        float max = 1f;
        foreach (Rgba128 p in pixels)
        {
            if (!float.IsFinite(p.R) || !float.IsFinite(p.G) || !float.IsFinite(p.B) || !float.IsFinite(p.A))
            {
                throw new ArgumentException("All pixel channels must be finite.", nameof(pixels));
            }

            max = Math.Max(max, Math.Max(p.R, Math.Max(p.G, p.B)));
        }

        Width = width;
        Height = height;
        _pixels = pixels.ToArray();
        ColorSpace = colorSpace;
        Headroom = headroom ?? max;
        GainMapKind = HdrGainMapKinds.None;
        Metadata = metadata?.Clone() ?? new ImageMetadata();
    }

    internal HdrImage(int width, int height, Rgba128[] pixels, HdrColorSpace colorSpace, float headroom, HdrGainMapKinds gainMapKind,
        ImageMetadata metadata)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
        ColorSpace = colorSpace;
        Headroom = headroom;
        GainMapKind = gainMapKind;
        Metadata = metadata;
    }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Width and height.</summary>
    public Size Size => new(Width, Height);

    /// <summary>The rectangle (0, 0, <see cref="Width"/>, <see cref="Height"/>).</summary>
    public Rectangle Bounds => new(0, 0, Width, Height);

    /// <summary>The linear-light color space of the pixels.</summary>
    public HdrColorSpace ColorSpace { get; }

    /// <summary>
    /// The headroom of this reconstruction as a linear ratio to SDR white (4.99 ≈ 2.32 stops): the source's maximum,
    /// limited by <c>HdrOptions.TargetHeadroom</c>, or the value given to the constructor. Gain map offsets and gamut
    /// conversion may put single components slightly above it.
    /// </summary>
    public float Headroom { get; }

    /// <summary>
    /// The gain map model used for the reconstruction (one flag), or <see cref="HdrGainMapKinds.None"/> for an image
    /// created from pixels.
    /// </summary>
    public HdrGainMapKinds GainMapKind { get; }

    /// <summary>
    /// A copy of the source image's metadata. The orientation is still pending in <see cref="ImageMetadata.Orientation"/>
    /// unless the image was loaded with <c>DecoderOptions.ApplyExifOrientation</c>.
    /// </summary>
    public ImageMetadata Metadata { get; }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public Rgba128 this[int x, int y]
    {
        get
        {
            ThrowIfDisposed();
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)x, (uint)Width, nameof(x));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
            return _pixels[((long)y * Width) + x];
        }
    }

    /// <summary>One row of pixels.</summary>
    public ReadOnlySpan<Rgba128> GetRowSpan(int y)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)y, (uint)Height, nameof(y));
        return _pixels.AsSpan(y * Width, Width);
    }

    /// <summary>All pixels, row by row without padding.</summary>
    public ReadOnlySpan<Rgba128> GetPixelSpan()
    {
        ThrowIfDisposed();
        return _pixels;
    }

    /// <summary>All pixels, row by row without padding, for use across <c>await</c>.</summary>
    public ReadOnlyMemory<Rgba128> GetPixelMemory()
    {
        ThrowIfDisposed();
        return _pixels;
    }

    /// <summary>Copies all pixels to <paramref name="destination"/>, which needs room for <c>Width × Height</c> pixels.</summary>
    public void CopyPixelDataTo(Span<Rgba128> destination)
    {
        ThrowIfDisposed();
        if (destination.Length < _pixels.Length)
        {
            throw new ArgumentException($"The destination needs {_pixels.Length} pixels.", nameof(destination));
        }

        _pixels.CopyTo(destination);
    }

    /// <summary>
    /// Ends the use of the image; later pixel access throws <see cref="ObjectDisposedException"/>. Views obtained
    /// earlier remain readable, as with <see cref="Image"/>.
    /// </summary>
    public void Dispose() => _disposed = true;

    /// <inheritdoc/>
    public override string ToString() =>
        _disposed ? "HdrImage (disposed)"
            : string.Create(CultureInfo.InvariantCulture, $"HdrImage {Width}x{Height}, {ColorSpace}, headroom {Headroom:0.###}");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
