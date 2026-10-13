using System.Runtime.CompilerServices;
using RasterSharp.Formats;

namespace RasterSharp;

public sealed partial class Image
{
    /// <summary>Decodes an image file (WebP, PNG, JPEG or BMP; the format is detected from content).</summary>
    public static Image Load(string path, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Decode(File.ReadAllBytes(path), options);
    }

    /// <summary>Decodes an image from a stream, reading it to the end. The stream need not be seekable.</summary>
    public static Image Load(Stream stream, DecoderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Decode(StreamHelper.ReadAll(stream), options);
    }

    /// <summary>Decodes an image from memory (the data is copied).</summary>
    public static Image Load(ReadOnlySpan<byte> data, DecoderOptions? options = null) => Decode(data.ToArray(), options);

    /// <summary>Decodes an image from memory without copying the buffer. Byte arrays bind to this overload.</summary>
    [OverloadResolutionPriority(1)]
    public static Image Load(ReadOnlyMemory<byte> data, DecoderOptions? options = null) => Decode(data, options);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public static Task<Image> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        LoadAsync(path, null, cancellationToken);

    /// <summary>Reads a file asynchronously and decodes it.</summary>
    public static async Task<Image> LoadAsync(string path, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return Decode(data, options);
    }

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public static Task<Image> LoadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        LoadAsync(stream, null, cancellationToken);

    /// <summary>Reads a stream asynchronously to the end and decodes it.</summary>
    public static async Task<Image> LoadAsync(Stream stream, DecoderOptions? options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = await StreamHelper.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return Decode(data, options);
    }

    /// <summary>Creates an image from raw pixel data.</summary>
    /// <param name="pixels">Pixel rows, <paramref name="stride"/> bytes apart.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Layout of <paramref name="pixels"/>.</param>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    public static Image LoadPixelData(ReadOnlySpan<byte> pixels, int width, int height, PixelFormat format, int stride = 0)
    {
        var image = new Image([ImageFrame.CreateUninitialized(width, height)]) { HasAlpha = format.HasAlpha() };
        image.CopyPixelDataFrom(pixels, format, stride);
        return image;
    }

    /// <summary>Creates an image from <see cref="Rgba32"/> pixels, row by row without padding.</summary>
    public static Image LoadPixelData(ReadOnlySpan<Rgba32> pixels, int width, int height)
    {
        ImageFrame frame = ImageFrame.CreateUninitialized(width, height);
        if (pixels.Length < frame.Pixels.Length)
        {
            throw new ArgumentException($"The buffer is too small: {frame.Pixels.Length} pixels required, {pixels.Length} provided.", nameof(pixels));
        }

        pixels[..frame.Pixels.Length].CopyTo(frame.Pixels);
        return new Image([frame]) { HasAlpha = true };
    }

    /// <summary>Reads the dimensions and basic properties of an image file without decoding its pixels.</summary>
    public static ImageInfo Identify(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return CodecRegistry.Identify(File.ReadAllBytes(path), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return CodecRegistry.Identify(StreamHelper.ReadAll(stream), DecoderOptions.Default);
    }

    /// <summary>Reads the dimensions and basic properties of an encoded image without decoding its pixels.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> data) => CodecRegistry.Identify(data.ToArray(), DecoderOptions.Default);

    /// <summary>Reads a file asynchronously and returns its basic properties without decoding its pixels.</summary>
    public static async Task<ImageInfo> IdentifyAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return CodecRegistry.Identify(data, DecoderOptions.Default);
    }

    /// <summary>Reads a stream asynchronously to the end and returns its basic properties without decoding its pixels.</summary>
    public static async Task<ImageInfo> IdentifyAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] data = await StreamHelper.ReadAllAsync(stream, cancellationToken).ConfigureAwait(false);
        return CodecRegistry.Identify(data, DecoderOptions.Default);
    }

    /// <summary>Detects the format from the first bytes of encoded data (12 bytes are enough for every format).</summary>
    public static ImageFormat? DetectFormat(ReadOnlySpan<byte> header) => FormatDetector.Detect(header);

    /// <summary>Detects the format of a file from its first bytes.</summary>
    public static ImageFormat? DetectFormat(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var handle = File.OpenHandle(path);
        Span<byte> header = stackalloc byte[FormatDetector.HeaderSize];
        int read = 0;
        int n;
        while (read < header.Length && (n = RandomAccess.Read(handle, header[read..], read)) > 0)
        {
            read += n;
        }

        return FormatDetector.Detect(header[..read]);
    }

    /// <summary>
    /// Detects the format from the next bytes of <paramref name="stream"/>. A seekable stream is rewound to where it
    /// started; a non-seekable stream loses the bytes that were read.
    /// </summary>
    public static ImageFormat? DetectFormat(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long start = stream.CanSeek ? stream.Position : 0;
        Span<byte> header = stackalloc byte[FormatDetector.HeaderSize];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        return FormatDetector.Detect(header[..read]);
    }

    /// <summary>Builds an animation from the root frames of <paramref name="frames"/>, which must all have the same size.</summary>
    [OverloadResolutionPriority(1)]
    public static Image CreateAnimation(TimeSpan frameDuration, params ReadOnlySpan<Image> frames)
    {
        var list = new List<ImageFrame>(frames.Length);
        bool hasAlpha = false;
        foreach (Image source in frames)
        {
            hasAlpha |= AddAnimationFrame(list, source, frameDuration);
        }

        return CreateAnimation(list, hasAlpha);
    }

    /// <summary>Builds an animation from the root frames of <paramref name="frames"/>, which must all have the same size.</summary>
    public static Image CreateAnimation(TimeSpan frameDuration, IEnumerable<Image> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var list = new List<ImageFrame>();
        bool hasAlpha = false;
        foreach (Image source in frames)
        {
            hasAlpha |= AddAnimationFrame(list, source, frameDuration);
        }

        return CreateAnimation(list, hasAlpha);
    }

    private static bool AddAnimationFrame(List<ImageFrame> list, Image source, TimeSpan frameDuration)
    {
        ArgumentNullException.ThrowIfNull(source, "frames");
        ImageFrame frame = source.RootFrame.Clone();
        frame.Duration = frameDuration;
        if (list.Count > 0 && (frame.Width != list[0].Width || frame.Height != list[0].Height))
        {
            throw new ArgumentException("All frames must have the same size.", "frames");
        }

        list.Add(frame);
        return source.HasAlpha;
    }

    private static Image CreateAnimation(List<ImageFrame> list, bool hasAlpha) =>
        list.Count == 0
            ? throw new ArgumentException("At least one frame is required.", "frames")
            : new Image(list) { HasAlpha = hasAlpha };

    private static Image Decode(ReadOnlyMemory<byte> data, DecoderOptions? options) =>
        CodecRegistry.Decode(data, options ?? DecoderOptions.Default);
}

internal static class StreamHelper
{
    public static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> segment))
        {
            byte[] copy = segment.AsSpan((int)ms.Position).ToArray();
            ms.Position = ms.Length;
            return copy;
        }

        using var buffer = new MemoryStream(stream.CanSeek ? (int)Math.Max(0, Math.Min(int.MaxValue, stream.Length - stream.Position)) : 0);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }
}
