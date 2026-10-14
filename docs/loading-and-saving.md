# Loading and saving

## Loading

```csharp
using RasterSharp;

using var fromFile = Image.Load("input.webp");
using var fromStream = Image.Load(stream);               // read to the end; seeking is not needed
using var fromBytes = Image.Load(bytes);                 // byte[]: used without copying
using var fromSpan = Image.Load(bytes.AsSpan(16));       // span: copied
using var fromAsync = await Image.LoadAsync("input.png", cancellationToken);
```

| Method | Notes |
| --- | --- |
| `Image.Load(string path, DecoderOptions? options = null)` | Reads the whole file. |
| `Image.Load(Stream stream, DecoderOptions? options = null)` | Reads from the current position to the end; the stream is left open. |
| `Image.Load(ReadOnlyMemory<byte> data, ...)` | A `byte[]` binds here and is not copied. Keep it unchanged while the image is decoded. |
| `Image.Load(ReadOnlySpan<byte> data, ...)` | The data is copied first. |
| `Image.LoadAsync(string path \| Stream stream [, DecoderOptions? options], CancellationToken ct = default)` | Asynchronous I/O, then decoding. |
| `Image.LoadPixelData(...)` | Creates an image from raw pixels; see [Pixels and interop](pixels-and-interop.md). |
| `new Image(width, height [, background])` | A blank image, transparent unless a background is given. |

The format is always detected from the content: a PNG named `photo.jpg` loads fine. `image.SourceFormat` tells which format was decoded (`null` for images created in memory).

### Decoder options

```csharp
var options = new DecoderOptions
{
    ApplyExifOrientation = true,     // rotate camera photos upright while loading
    FirstFrameOnly = true,           // skip the rest of an animation
    IgnoreMetadata = true,           // do not keep ICC/EXIF/XMP
    MaxPixels = 40_000_000,          // reject larger images
};
using var image = Image.Load("upload.jpg", options);
```

| Property | Default | Meaning |
| --- | --- | --- |
| `MaxPixels` | 2^28 | Maximum width × height; larger images throw `ImageFormatException` before pixels are allocated. For GIF the limit covers all decoded frames together, because tiny GIF frames still decode to full canvases. |
| `FirstFrameOnly` | false | Decode only the first frame of animated WebP/APNG/GIF. |
| `ApplyExifOrientation` | false | Same as calling `Mutate(x => x.AutoOrient())` after loading. |
| `IgnoreMetadata` | false | Do not read ICC, EXIF and XMP blocks. |
| `StrictChecksums` | false | PNG: fail on CRC mismatches instead of ignoring them. |
| `WebPFancyUpsampling` | true | WebP lossy: smooth chroma upsampling, as libwebp. |
| `WebPBypassFiltering` | false | WebP lossy: skip the deblocking filter (faster, lower quality). |
| `MaxDegreeOfParallelism` | -1 | Threads to use; -1 is automatic, 1 is single-threaded. Decoded pixels do not depend on it. |

`DecoderOptions.Default` is a shared instance with the defaults. Options are records, so `DecoderOptions.Default with { MaxPixels = 1_000_000 }` creates a variant.

## Inspecting without decoding

```csharp
ImageInfo info = Image.Identify("photo.webp");                 // also Stream, ReadOnlyMemory<byte>, ReadOnlySpan<byte>; IdentifyAsync
Console.WriteLine($"{info.Format.Name} {info.Width}x{info.Height}, alpha: {info.HasAlpha}, frames: {info.FrameCount}");

ImageFormat? format = Image.DetectFormat("unknown.bin");       // first 12 bytes; also Stream and ReadOnlySpan<byte>
if (format == ImageFormat.Png) { /* ... */ }
```

`Identify(ReadOnlyMemory<byte>)` does not copy the input buffer; `byte[]` binds to this overload. Keep the input unchanged during the call. The span overload copies the input.

`ImageInfo` also reports `IsAnimated`, `BitsPerChannel`, `Orientation`, the resolution and, for WebP, `WebPCompression` (lossy, lossless or mixed). `DetectFormat(Stream)` rewinds seekable streams to where they were.

`ImageFormat` describes a format: `Name`, `MimeType`, `FileExtensions`, `CanDecode`, `CanEncode`. Look formats up with `ImageFormat.FromExtension("photo.JPG")` or `ImageFormat.FromMimeType("image/webp")`; `ImageFormat.All` lists them.

## Saving

```csharp
image.Save("out.webp");                                        // format from the extension, default options
image.Save("out.bin", new PngEncoderOptions());                // the options decide the format
image.Save(stream, ImageFormat.Jpeg);                          // default JPEG options
image.Save(stream, new WebPEncoderOptions { Lossless = true });
await image.SaveAsync("out.png", cancellationToken);
```

Each format also has dedicated methods, which accept a path or a stream and optional options (`null` means the defaults):

```csharp
image.SaveAsWebP("photo.webp", new() { Quality = 82 });
image.SaveAsPng(stream);
await image.SaveAsJpegAsync("photo.jpg", new() { Quality = 90, Progressive = true });
await image.SaveAsBmpAsync(stream, cancellationToken: ct);
byte[] webp = image.ToWebP();
byte[] png = image.ToPng(new() { CompressionLevel = 9 });
byte[] encoded = image.Encode(ImageFormat.Png);                // runtime format, default options
byte[] custom = image.Encode(new PngEncoderOptions { CompressionLevel = 9 });
```

| Format | Path / stream | Async | To bytes |
| --- | --- | --- | --- |
| WebP | `SaveAsWebP` | `SaveAsWebPAsync` | `ToWebP` |
| PNG | `SaveAsPng` | `SaveAsPngAsync` | `ToPng` |
| JPEG | `SaveAsJpeg` | `SaveAsJpegAsync` | `ToJpeg` |
| BMP | `SaveAsBmp` | `SaveAsBmpAsync` | `ToBmp` |
| GIF | `SaveAsGif` | `SaveAsGifAsync` | `ToGif` |

Streams are left open and do not need to be seekable. Defaults: WebP lossy quality 75, method 4; PNG compression 6 with automatic color type; JPEG quality 75, 4:2:0; BMP automatic bit depth; GIF up to 256 colors per frame, alpha threshold 128. WebP, PNG and GIF keep all animation frames; JPEG and BMP write the first frame. All option settings are in [Encoder options](encoder-options.md).

## Recipes

### Convert a folder to WebP

```csharp
foreach (string path in Directory.EnumerateFiles("photos"))
{
    if (ImageFormat.FromExtension(path) is not { CanDecode: true })
    {
        continue;
    }

    using var image = Image.Load(path, new DecoderOptions { ApplyExifOrientation = true });
    image.Mutate(x => x.Thumbnail(2048, 2048));
    image.SaveAsWebP(Path.ChangeExtension(path, ".webp"), new() { Quality = 80 });
}
```

### Serve a resized image from ASP.NET Core

```csharp
app.MapGet("/thumb/{name}", async (string name, int? w, CancellationToken ct) =>
{
    using var image = await Image.LoadAsync(Path.Combine("images", Path.GetFileName(name)), ct);
    image.Mutate(x => x.AutoOrient().Thumbnail(w ?? 400, 0));
    return Results.File(image.ToWebP(new() { Quality = 80 }), ImageFormat.WebP.MimeType);
});
```

### Choose the output format from a MIME type

```csharp
ImageFormat target = ImageFormat.FromMimeType(request.Accept) ?? ImageFormat.Png;
image.Save(response.Body, target);
```

### WebP conversion

`WebPConverter.Convert(inputPath, outputPath, options)` preserves the `gif2webp` conversion conventions: GIF defaults to lossless and delays of 10 ms or less become 100 ms. Ordinary `Image.Load(...).SaveAsWebP(...)` preserves the original frame durations. See [Animation](animation.md).
