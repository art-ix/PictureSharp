using Microsoft.AspNetCore.Http;

namespace PictureSharp.AspNetCore;

/// <summary>Returns images from endpoints and reads uploaded images.</summary>
public static class ImageHttpExtensions
{
    extension(IResultExtensions results)
    {
        /// <summary>
        /// Returns <paramref name="image"/> in the format negotiated from <c>Accept</c>:
        /// <c>Results.Extensions.Image(image)</c>. See <see cref="ImageResult"/>.
        /// </summary>
        /// <param name="image">The image to send.</param>
        /// <param name="options">Formats and headers; null means <see cref="ImageResultOptions.Default"/>.</param>
        /// <param name="disposeImage">True (the default) disposes the image after the response.</param>
        public ImageResult Image(Image image, ImageResultOptions? options = null, bool disposeImage = true)
        {
            ArgumentNullException.ThrowIfNull(results);
            return new ImageResult(image, options, disposeImage);
        }
    }

    extension(HttpRequest request)
    {
        /// <summary>
        /// Decodes the request body with <paramref name="options"/> (null means <see cref="DecoderOptions.Default"/>),
        /// stopping when the request is aborted or <paramref name="cancellationToken"/> is canceled. A body larger than
        /// <see cref="DecoderOptions.MaxInputBytes"/>, by <c>Content-Length</c> or while reading, throws
        /// <see cref="BadHttpRequestException"/> with status 413 before it is buffered. The other limits and errors are
        /// those of <c>Image.LoadAsync</c>: corrupt or unrecognized data throws <see cref="ImageFormatException"/> and an
        /// unsupported variant <see cref="NotSupportedException"/>.
        /// </summary>
        public async Task<Image> ReadImageAsync(DecoderOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            options ??= DecoderOptions.Default;
            if (request.ContentLength > options.MaxInputBytes)
            {
                throw TooLarge(options.MaxInputBytes);
            }

            using CancellationTokenSource? linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, cancellationToken)
                : null;
            var body = new LimitedReadStream(request.Body, options.MaxInputBytes);
            return await PictureSharp.Image.LoadAsync(body, options, linked?.Token ?? request.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    internal static BadHttpRequestException TooLarge(long limit) =>
        new($"The request body exceeds {limit} bytes (DecoderOptions.MaxInputBytes).", StatusCodes.Status413PayloadTooLarge);
}
