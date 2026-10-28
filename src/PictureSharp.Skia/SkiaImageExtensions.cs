using System.Buffers;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PictureSharp.Skia;

/// <summary>
/// Copies <see cref="Image"/> pixels to and from SkiaSharp. Every conversion copies: the result is owned by the caller
/// and later changes to either side do not affect the other. PictureSharp pixels have straight alpha; the
/// <see cref="SKAlphaType"/> argument decides whether Skia receives straight (<see cref="SKAlphaType.Unpremul"/>)
/// or premultiplied (<see cref="SKAlphaType.Premul"/>) colors. Color values are copied without color-space
/// conversion; call <c>ConvertToSrgb()</c> first when the image carries another profile.
/// </summary>
public static class SkiaImageExtensions
{
    extension(Image image)
    {
        /// <summary>Copies the first frame into a new raster bitmap.</summary>
        /// <param name="colorType"><see cref="SKColorType.Rgba8888"/> (no swizzle) or <see cref="SKColorType.Bgra8888"/>.</param>
        /// <param name="alphaType"><see cref="SKAlphaType.Unpremul"/> (exact) or <see cref="SKAlphaType.Premul"/>.</param>
        /// <exception cref="ArgumentOutOfRangeException">The color or alpha type is not one of the above.</exception>
        public SKBitmap ToSKBitmap(SKColorType colorType = SKColorType.Rgba8888, SKAlphaType alphaType = SKAlphaType.Unpremul)
        {
            ArgumentNullException.ThrowIfNull(image);
            return image.Frames.RootFrame.ToSKBitmap(colorType, alphaType);
        }

        /// <summary>Copies the first frame into a new immutable raster image; see <c>ToSKBitmap</c> for the arguments.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The color or alpha type is not supported.</exception>
        public SKImage ToSKImage(SKColorType colorType = SKColorType.Rgba8888, SKAlphaType alphaType = SKAlphaType.Unpremul)
        {
            ArgumentNullException.ThrowIfNull(image);
            return image.Frames.RootFrame.ToSKImage(colorType, alphaType);
        }
    }

    extension(ImageFrame frame)
    {
        /// <summary>Copies this frame into a new raster bitmap; see <c>Image.ToSKBitmap</c> for the arguments.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The color or alpha type is not supported.</exception>
        public SKBitmap ToSKBitmap(SKColorType colorType = SKColorType.Rgba8888, SKAlphaType alphaType = SKAlphaType.Unpremul)
        {
            ArgumentNullException.ThrowIfNull(frame);
            PixelFormat format = Layout(colorType, alphaType);
            var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, colorType, alphaType));
            try
            {
                if (bitmap.GetPixels() == IntPtr.Zero)
                {
                    throw new OutOfMemoryException($"SkiaSharp could not allocate a {frame.Width}x{frame.Height} bitmap.");
                }

                frame.CopyPixelDataTo(bitmap.GetPixelSpan(), format, bitmap.RowBytes);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        /// <summary>Copies this frame into a new immutable raster image; see <c>Image.ToSKBitmap</c> for the arguments.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The color or alpha type is not supported.</exception>
        public SKImage ToSKImage(SKColorType colorType = SKColorType.Rgba8888, SKAlphaType alphaType = SKAlphaType.Unpremul)
        {
            ArgumentNullException.ThrowIfNull(frame);
            PixelFormat format = Layout(colorType, alphaType);
            var info = new SKImageInfo(frame.Width, frame.Height, colorType, alphaType);
            if (format == PixelFormat.Rgba32)
            {
                return Created(SKImage.FromPixelCopy(info, frame.GetPixelByteSpan(), info.RowBytes), info);
            }

            int length = info.BytesSize;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                frame.CopyPixelDataTo(buffer.AsSpan(0, length), format);
                return Created(SKImage.FromPixelCopy(info, buffer.AsSpan(0, length), info.RowBytes), info);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    extension(Image)
    {
        /// <summary>
        /// Copies the pixels of a raster bitmap into a new image with straight alpha. Any color and alpha type is
        /// accepted: Skia converts it to <see cref="SKColorType.Rgba8888"/> with <see cref="SKAlphaType.Unpremul"/>.
        /// </summary>
        /// <exception cref="ArgumentException">The bitmap is empty or has no readable pixels.</exception>
        public static Image FromSKBitmap(SKBitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            using SKPixmap? pixmap = bitmap.PeekPixels();
            if (pixmap is null)
            {
                throw new ArgumentException("The bitmap has no readable pixels.", nameof(bitmap));
            }

            return Read(pixmap.Info, pixmap, static (source, destination) => source.ReadPixels(destination), nameof(bitmap));
        }

        /// <summary>Copies the pixels of a pixmap into a new image with straight alpha; any color and alpha type is accepted.</summary>
        /// <exception cref="ArgumentException">The pixmap is empty or its pixels cannot be converted.</exception>
        public static Image FromSKPixmap(SKPixmap pixmap)
        {
            ArgumentNullException.ThrowIfNull(pixmap);
            return Read(pixmap.Info, pixmap, static (source, destination) => source.ReadPixels(destination), nameof(pixmap));
        }

        /// <summary>
        /// Copies the pixels of an image into a new <see cref="Image"/> with straight alpha. A raster or lazily decoded
        /// image works; a texture-backed image must be read through its GPU context first.
        /// </summary>
        /// <exception cref="ArgumentException">The image is empty or its pixels cannot be read.</exception>
        public static Image FromSKImage(SKImage image)
        {
            ArgumentNullException.ThrowIfNull(image);
            return Read(image.Info, image, static (source, destination) => source.ReadPixels(destination, 0, 0), nameof(image));
        }
    }

    private static PixelFormat Layout(SKColorType colorType, SKAlphaType alphaType) => (colorType, alphaType) switch
    {
        (SKColorType.Rgba8888, SKAlphaType.Unpremul) => PixelFormat.Rgba32,
        (SKColorType.Rgba8888, SKAlphaType.Premul) => PixelFormat.Rgba32Premultiplied,
        (SKColorType.Bgra8888, SKAlphaType.Unpremul) => PixelFormat.Bgra32,
        (SKColorType.Bgra8888, SKAlphaType.Premul) => PixelFormat.Bgra32Premultiplied,
        (not (SKColorType.Rgba8888 or SKColorType.Bgra8888), _) =>
            throw new ArgumentOutOfRangeException(nameof(colorType), colorType, "Use SKColorType.Rgba8888 or SKColorType.Bgra8888."),
        _ => throw new ArgumentOutOfRangeException(nameof(alphaType), alphaType, "Use SKAlphaType.Unpremul or SKAlphaType.Premul."),
    };

    private static SKImage Created(SKImage? image, SKImageInfo info) =>
        image ?? throw new OutOfMemoryException($"SkiaSharp could not create a {info.Width}x{info.Height} image.");

    // Skia writes straight RGBA directly into the pinned pixel array of the new image.
    private static Image Read<TSource>(SKImageInfo sourceInfo, TSource source, Func<TSource, SKPixmap, bool> read, string parameter)
    {
        if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0)
        {
            throw new ArgumentException("The source has no pixels.", parameter);
        }

        var image = new Image(sourceInfo.Width, sourceInfo.Height);
        try
        {
            var info = new SKImageInfo(sourceInfo.Width, sourceInfo.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            // Image pixels are always one managed array (GC.AllocateUninitializedArray).
            if (!MemoryMarshal.TryGetArray<Rgba32>(image.GetPixelMemory<Rgba32>(), out ArraySegment<Rgba32> pixels))
            {
                throw new InvalidOperationException("The image pixels are not backed by an array.");
            }

            bool done;
            GCHandle handle = GCHandle.Alloc(pixels.Array!, GCHandleType.Pinned);
            try
            {
                using var destination = new SKPixmap(info, handle.AddrOfPinnedObject() + (pixels.Offset * 4), info.RowBytes);
                done = read(source, destination);
            }
            finally
            {
                handle.Free();
            }

            if (!done)
            {
                throw new ArgumentException("SkiaSharp could not convert the pixels to RGBA.", parameter);
            }

            // Skia copies the undefined fourth byte of an opaque 8888 source unchanged.
            if (sourceInfo.AlphaType == SKAlphaType.Opaque)
            {
                foreach (ref Rgba32 pixel in image.GetPixelSpan<Rgba32>()) pixel.A = 255;
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }
}
