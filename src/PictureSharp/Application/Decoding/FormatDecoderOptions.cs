namespace PictureSharp.Formats;

/// <summary>
/// Base type of the decoding settings of a plugin format, passed through <see cref="DecoderOptions.FormatOptions"/>.
/// A plugin derives a record from it (for example a page or element to decode) and reads it with
/// <see cref="DecoderOptions.GetFormatOptions{T}"/> in <see cref="IImageFormatDecoder.Identify"/>,
/// <see cref="IImageFormatDecoder.Decode"/> and its <see cref="IImageFormatMetadataCodec"/>. Built-in formats and other
/// plugins ignore it.
/// </summary>
/// <example>
/// <code>
/// public sealed record TiffDecoderOptions : FormatDecoderOptions
/// {
///     public int Page { get; init => field = value &gt;= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Page)); }
/// }
///
/// using Image page = Image.Load(path, new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 2 }] });
/// </code>
/// </example>
public abstract record FormatDecoderOptions;
