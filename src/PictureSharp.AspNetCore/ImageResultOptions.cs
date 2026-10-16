namespace PictureSharp.AspNetCore;

/// <summary>How an <see cref="ImageResult"/> chooses the response format and which headers it writes.</summary>
public sealed record ImageResultOptions
{
    /// <summary>The defaults: WebP, PNG and JPEG with their default encoder options, no <c>Cache-Control</c>.</summary>
    public static ImageResultOptions Default { get; } = new();

    /// <summary>
    /// The formats offered to the client, most preferred first. The format with the highest <c>Accept</c> quality wins;
    /// equal qualities, a missing <c>Accept</c> header and <c>*/*</c> select the earliest one. Must be non-empty and
    /// encodable. Default: WebP, PNG, JPEG.
    /// </summary>
    public IReadOnlyList<ImageFormat> Formats
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count == 0) throw new ArgumentException("At least one format is required.", nameof(Formats));
            foreach (ImageFormat format in value)
            {
                ArgumentNullException.ThrowIfNull(format, nameof(Formats));
                if (!format.CanEncode) throw new ArgumentException($"{format.Name} cannot be encoded.", nameof(Formats));
            }

            field = [.. value];
        }
    } = [ImageFormat.WebP, ImageFormat.Png, ImageFormat.Jpeg];

    /// <summary>
    /// Encoder options used when their <see cref="EncoderOptions.Format"/> is selected, for example
    /// <c>new WebPEncoderOptions { Quality = 80 }</c>. Other formats use their default options. Default: empty.
    /// </summary>
    public IReadOnlyList<EncoderOptions> Encoders
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (EncoderOptions options in value) ArgumentNullException.ThrowIfNull(options, nameof(Encoders));
            field = [.. value];
        }
    } = [];

    /// <summary>The <c>Cache-Control</c> header value, for example <c>public, max-age=86400</c>; null writes none. Default: null.</summary>
    public string? CacheControl { get; init; }

    /// <summary>
    /// When the <c>Accept</c> header excludes every format: true responds 406 Not Acceptable, false (the default) sends
    /// the first format, as MVC does without <c>ReturnHttpNotAcceptable</c>.
    /// </summary>
    public bool RespondNotAcceptable { get; init; }
}
