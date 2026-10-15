namespace PictureSharp.Application.Abstractions;

internal readonly record struct DecodedImage(Image Image, System.Drawing.Size StoredSize, int Scale = 1);
