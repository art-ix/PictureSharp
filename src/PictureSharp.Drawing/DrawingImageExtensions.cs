using System.Drawing.Imaging;
using System.Runtime.Versioning;
using Bitmap = System.Drawing.Bitmap;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;
using Rectangle = System.Drawing.Rectangle;

namespace PictureSharp.Drawing;

/// <summary>
/// Copies <see cref="Image"/> pixels to and from <see cref="Bitmap"/>. Every conversion copies: the result is owned by
/// the caller and later changes to either side do not affect the other. GDI+ is supported on Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DrawingImageExtensions
{
    extension(Image image)
    {
        /// <summary>
        /// Copies the first frame into a new bitmap. The default <see cref="GdiPixelFormat.Format32bppArgb"/> keeps
        /// straight alpha exactly; <see cref="GdiPixelFormat.Format32bppPArgb"/> premultiplies (faster to draw with GDI+);
        /// <see cref="GdiPixelFormat.Format24bppRgb"/> and <see cref="GdiPixelFormat.Format32bppRgb"/> drop alpha
        /// without compositing. The bitmap gets the image resolution.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not one of the four formats above.</exception>
        public Bitmap ToBitmap(GdiPixelFormat format = GdiPixelFormat.Format32bppArgb)
        {
            ArgumentNullException.ThrowIfNull(image);
            Bitmap bitmap = image.Frames.RootFrame.ToBitmap(format);
            if (image.Metadata.HorizontalResolution > 0 && image.Metadata.VerticalResolution > 0)
            {
                bitmap.SetResolution(image.Metadata.HorizontalResolution, image.Metadata.VerticalResolution);
            }

            return bitmap;
        }
    }

    extension(ImageFrame frame)
    {
        /// <summary>Copies this frame into a new bitmap; see <c>Image.ToBitmap</c> for the supported formats.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not supported.</exception>
        public Bitmap ToBitmap(GdiPixelFormat format = GdiPixelFormat.Format32bppArgb)
        {
            ArgumentNullException.ThrowIfNull(frame);
            PixelFormat layout = format switch
            {
                GdiPixelFormat.Format32bppArgb or GdiPixelFormat.Format32bppRgb => PixelFormat.Bgra32,
                GdiPixelFormat.Format32bppPArgb => PixelFormat.Bgra32Premultiplied,
                GdiPixelFormat.Format24bppRgb => PixelFormat.Bgr24,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Use Format32bppArgb, Format32bppPArgb, Format32bppRgb or Format24bppRgb."),
            };
            var bitmap = new Bitmap(frame.Width, frame.Height, format);
            try
            {
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, format);
                try
                {
                    BitmapPixels.Write(frame, data.Scan0, data.Stride, layout);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    extension(Image)
    {
        /// <summary>
        /// Copies the active frame of <paramref name="bitmap"/> into a new image with straight alpha. 32-bit ARGB,
        /// premultiplied ARGB and 24/32-bit RGB are converted by PictureSharp; other formats (indexed, 16-bit, 48/64-bit)
        /// are converted to 32-bit ARGB by GDI+ first. The image gets the bitmap resolution.
        /// </summary>
        public static Image FromBitmap(Bitmap bitmap)
        {
            ArgumentNullException.ThrowIfNull(bitmap);
            (GdiPixelFormat lockFormat, PixelFormat layout) = bitmap.PixelFormat switch
            {
                GdiPixelFormat.Format32bppPArgb => (GdiPixelFormat.Format32bppPArgb, PixelFormat.Bgra32Premultiplied),
                // The fourth byte of 32bppRgb is undefined; GDI+ drops it when locking as 24-bit.
                GdiPixelFormat.Format24bppRgb or GdiPixelFormat.Format32bppRgb => (GdiPixelFormat.Format24bppRgb, PixelFormat.Bgr24),
                _ => (GdiPixelFormat.Format32bppArgb, PixelFormat.Bgra32),
            };
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, lockFormat);
            Image image;
            try
            {
                image = BitmapPixels.Read(data.Scan0, data.Stride, bitmap.Width, bitmap.Height, layout);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            if (bitmap.HorizontalResolution is > 0 and < float.PositiveInfinity && bitmap.VerticalResolution is > 0 and < float.PositiveInfinity)
            {
                image.Metadata.HorizontalResolution = bitmap.HorizontalResolution;
                image.Metadata.VerticalResolution = bitmap.VerticalResolution;
            }

            return image;
        }
    }
}
