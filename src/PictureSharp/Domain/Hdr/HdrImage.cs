using System.Drawing;
using System.Globalization;

namespace PictureSharp;

/// <summary>
/// A high-dynamic-range image in linear light, reconstructed by <c>image.ToHdr()</c> from an SDR base image and its
/// gain map. Pixels are <see cref="Rgba128"/> with straight alpha; 1 is SDR white, components above 1 are highlights and
/// components below 0 may come from gamut conversion. Nothing is clipped. The pixels are read-only, which keeps
/// <see cref="Headroom"/> and <see cref="ColorSpace"/> valid; concurrent reads are safe. The image owns its buffer and
/// does not depend on the lifetime of the source image.
/// </summary>
public sealed class HdrImage : IDisposable
{
    private readonly Rgba128[] _pixels;
    private bool _disposed;

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
    /// limited by <c>HdrOptions.TargetHeadroom</c>. Gain map offsets and gamut conversion may put single components
    /// slightly above it.
    /// </summary>
    public float Headroom { get; }

    /// <summary>The gain map model used for the reconstruction (one flag).</summary>
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
