using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace PictureSharp.AspNetCore;

/// <summary>
/// An <see cref="IResult"/> that encodes an image in the format negotiated from the request <c>Accept</c> header.
/// The image is encoded into memory first, so a failed or canceled encoding sends no partial body and the response
/// carries <c>Content-Length</c>. Encoding and writing stop when <see cref="HttpContext.RequestAborted"/> is canceled.
/// </summary>
/// <remarks>
/// The response gets <c>Content-Type</c>, <c>Content-Length</c>, <c>Vary: Accept</c> when more than one format is offered
/// and the configured <c>Cache-Control</c>. A HEAD request receives the headers without the body. JPEG drops alpha;
/// order <see cref="ImageResultOptions.Formats"/> accordingly for transparent images.
/// </remarks>
public sealed class ImageResult : IResult
{
    /// <summary>Creates a result for <paramref name="image"/>.</summary>
    /// <param name="image">The image to send; animations are sent animated when the negotiated format supports it.</param>
    /// <param name="options">Formats and headers; null means <see cref="ImageResultOptions.Default"/>.</param>
    /// <param name="disposeImage">True (the default) disposes the image after the response is written or fails.</param>
    public ImageResult(Image image, ImageResultOptions? options = null, bool disposeImage = true)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
        Options = options ?? ImageResultOptions.Default;
        DisposeImage = disposeImage;
    }

    /// <summary>The image to send.</summary>
    public Image Image { get; }

    /// <summary>The formats offered and the headers written.</summary>
    public ImageResultOptions Options { get; }

    /// <summary>Whether <see cref="ExecuteAsync"/> disposes <see cref="Image"/>.</summary>
    public bool DisposeImage { get; }

    /// <summary>Encodes the image and writes the response.</summary>
    /// <exception cref="OperationCanceledException">The request was aborted.</exception>
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        try
        {
            CancellationToken cancellationToken = httpContext.RequestAborted;
            cancellationToken.ThrowIfCancellationRequested();
            HttpResponse response = httpContext.Response;
            if (Options.Formats.Count > 1 && !VariesByAccept(response.Headers.Vary))
            {
                response.Headers.Append(HeaderNames.Vary, HeaderNames.Accept);
            }

            ImageFormat? format = AcceptNegotiation.Select(Options.Formats, httpContext.Request.GetTypedHeaders().Accept);
            if (format is null)
            {
                if (Options.RespondNotAcceptable)
                {
                    response.StatusCode = StatusCodes.Status406NotAcceptable;
                    return;
                }

                format = Options.Formats[0];
            }

            using var encoded = new MemoryStream();
            if (Encoder(format) is { } encoder)
            {
                await Image.SaveAsync(encoded, encoder, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Image.SaveAsync(encoded, format, cancellationToken).ConfigureAwait(false);
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = format.MimeType;
            response.ContentLength = encoded.Length;
            if (Options.CacheControl is { } cacheControl)
            {
                response.Headers.CacheControl = cacheControl;
            }

            if (!HttpMethods.IsHead(httpContext.Request.Method))
            {
                await response.Body.WriteAsync(encoded.GetBuffer().AsMemory(0, (int)encoded.Length), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (DisposeImage) Image.Dispose();
        }
    }

    private static bool VariesByAccept(StringValues vary)
    {
        foreach (string? value in vary)
        {
            foreach (string token in (value ?? "").Split(',', StringSplitOptions.TrimEntries))
            {
                if (token == "*" || token.Equals(HeaderNames.Accept, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }

    private EncoderOptions? Encoder(ImageFormat format)
    {
        foreach (EncoderOptions options in Options.Encoders)
        {
            if (options.Format == format) return options;
        }

        return null;
    }
}
