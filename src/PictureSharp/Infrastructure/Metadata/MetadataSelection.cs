namespace PictureSharp.Infrastructure.Metadata;

/// <summary>Selects a nonempty metadata block without copying its contents.</summary>
internal static class MetadataSelection
{
    public static byte[]? Select(byte[]? data, MetadataKinds selection, MetadataKinds kind) =>
        (selection & kind) != 0 && data is { Length: > 0 } ? data : null;
}
