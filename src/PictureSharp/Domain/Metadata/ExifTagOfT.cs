namespace PictureSharp;

/// <summary>An EXIF tag whose value has the compile-time type <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The supported value type.</typeparam>
public sealed class ExifTag<T> : ExifTag where T : notnull
{
    internal ExifTag(ushort id, int directory, ushort type) : base(id, directory, type) { }
}
