using PictureSharp.Domain.Hdr;

namespace PictureSharp.Application.Abstractions;

/// <summary>
/// A decoder result with the stored size and JPEG scale; <see cref="GainMap"/> is attached to the image after the
/// load's thumbnail, color and orientation steps, which would otherwise remove it.
/// </summary>
internal readonly record struct DecodedImage(Image Image, System.Drawing.Size StoredSize, int Scale = 1)
{
    public GainMapAttachment? GainMap { get; init; }
}
