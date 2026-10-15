
namespace PictureSharp.Formats.WebP;

/// <summary>Converts images to WebP using the animation conventions of libwebp's gif2webp.</summary>
public static class WebPConverter
{
    /// <summary>
    /// Converts a supported image file to WebP. GIF files are converted like libwebp's gif2webp: frames shown for
    /// 10 ms or less get 100 ms (as browsers display them) and, without <paramref name="options"/>, the frames are
    /// lossless.
    /// </summary>
    public static void Convert(string inputPath, string outputPath, WebPEncoderOptions? options = null)
    {
        using Image image = Image.Load(inputPath);
        if (image.SourceFormat == ImageFormat.Gif)
        {
            options ??= new WebPEncoderOptions { Lossless = true };
            if (image.IsAnimated)
            {
                // port: examples/gif2webp.c main (frame duration adjustment)
                foreach (ImageFrame frame in image.Frames)
                {
                    if (frame.Duration <= TimeSpan.FromMilliseconds(10))
                    {
                        frame.Duration = TimeSpan.FromMilliseconds(100);
                    }
                }
            }
        }

        image.SaveAsWebP(outputPath, options);
    }
}
