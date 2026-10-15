# Loading and saving

## Loading

```csharp
using PictureSharp;

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
| `Image.LoadAsync(string path \| Stream stream [, DecoderOptions? options], CancellationToken ct = default)` | Asynchronous I/O, then decoding; the token also stops the decoding. |
| `Image.Load(..., DecoderOptions? options, CancellationToken ct)` | Every source also has an overload whose token stops the reading and decoding; see [Resource limits and cancellation](limits-and-cancellation.md). |
| `Image.LoadPixelData(...)` | Creates an image from raw pixels; see [Pixels and interop](pixels-and-interop.md). |
| `new Image(width, height [, background])` | A blank image, transparent unless a background is given. |

The format is always detected from the content: a PNG named `photo.jpg` loads fine. `image.SourceFormat` tells which format was decoded (`null` for images created in memory).

### PCX support

PCX loads through the same `Image.Load` methods. It supports monochrome, packed and planar palette images, grayscale, RGB and RGBA. DCX (multiple PCX images) and legacy color images below 8 bits per plane without palette information (versions 0 and 3) are not supported. See [PCX encoder options](encoder-options.md#pcx) for output formats and compatibility notes.

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
| `MaxInputBytes` | `Array.MaxLength` | Maximum size of the encoded input; reading stops with `ImageFormatException` as soon as more bytes arrive. |
| `MaxFrames` | 65536 | Maximum number of frames stored in an animation; checked before decoding, also by `Identify`. |
| `MaxMetadataBytes` | 64 MiB | Maximum total size of the ICC, EXIF and XMP blocks after decompression. |
| `MaxPixels` | 2^28 | Maximum width × height; larger images throw `ImageFormatException` before pixels are allocated. For animations (GIF, APNG, WebP) the limit covers all decoded frames together, because every decoded frame is a full canvas; `FirstFrameOnly` counts one frame. |
| `FirstFrameOnly` | false | Decode only the first frame of animated WebP/APNG/GIF. |
| `ApplyExifOrientation` | false | Same as calling `Mutate(x => x.AutoOrient())` after loading. |
| `TargetSize` | null | Shrink to fit inside this size while loading, like `Thumbnail`; JPEG decodes at reduced scale. See [Thumbnails while loading](#thumbnails-while-loading). |
| `TargetResampling` | `Lanczos3` | Filter of the resize to `TargetSize`. |
| `TargetReducingGap` | 2 | JPEG: how much larger than the final size a reduced decode must stay, per side (at least 1). |
| `IgnoreMetadata` | false | Do not read ICC, EXIF and XMP blocks. |
| `StrictChecksums` | false | PNG: fail on CRC mismatches instead of ignoring them. |
| `WebPFancyUpsampling` | true | WebP lossy: smooth chroma upsampling, as libwebp. |
| `WebPBypassFiltering` | false | WebP lossy: skip the deblocking filter (faster, lower quality). |
| `MaxDegreeOfParallelism` | -1 | Threads to use; -1 is automatic, 1 is single-threaded. Decoded pixels do not depend on it. |

`DecoderOptions.Default` is a shared instance with the defaults. Options are records, so `DecoderOptions.Default with { MaxPixels = 1_000_000 }` creates a variant.

### Thumbnails while loading

`TargetSize` loads an image already shrunk to fit inside a box, keeping its aspect ratio. The result has the size
`Thumbnail(width, height)` would give: 0 for one side means no limit on it, and an image that already fits keeps its
size (it is never enlarged).

```csharp
var options = new DecoderOptions { TargetSize = new(256, 256), ApplyExifOrientation = true };
using var thumbnail = Image.Load("photo.jpg", options);   // at most 256×256, upright
```

- **JPEG** is decoded at 1/2, 1/4 or 1/8 scale by a reduced inverse DCT (the same as libjpeg-turbo's
  `scale_denom`, bit-exact with it), then resampled to the final size. The decoder never builds the full-size RGBA image
  or full-size sample planes, which saves most of the time and memory for small targets; see the measurements in
  [Performance](performance.md#jpeg-thumbnails-in-054). The scale is the largest that keeps `TargetReducingGap` times the
  final size on both axes (2 by default, as Pillow's `thumbnail` and libvips do); 1 saves the most and is slightly
  sharper, larger values give the resampling filter more to work with. Baseline and progressive files, grayscale, CMYK
  and every chroma subsampling are supported.
- **Progressive JPEG** still keeps the coefficients of the whole image (2 bytes per sample, e.g. 36 MB for a 12 MP
  4:2:0 photo) because later scans refine every block; only the sample planes and pixels shrink.
- **Other formats** (PNG, WebP, GIF, BMP, ICO, PCX) are decoded in full and then resized with `Thumbnail`, exactly as
  `Load` followed by `Mutate(x => x.Thumbnail(...))`; there is no memory saving. Every frame of an animation is resized.
- **Orientation.** The box applies to the returned image: with `ApplyExifOrientation` a portrait photo stored
  sideways (orientations 5–8) fits the box upright. The resize runs before the orientation is applied, on the smaller
  image.
- **Not identical to `Load` + `Thumbnail`.** A reduced JPEG decode is a different algorithm, so the pixels differ
  slightly from a full decode followed by a resize (53–56 dB PSNR on 12–27 MP photos with the default gap, 39–53 dB
  with gap 1; at least 30 dB on the small synthetic corpus); the size is the same. When the scale works
  out to 1 (the box is less than `TargetReducingGap` times smaller), the result equals `Load` + `Thumbnail`.
- **Limits** still check the stored size: `MaxPixels` rejects a huge JPEG even when the target is tiny.
  `Identify` reports the stored size.

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
| PCX | `SaveAsPcx` | `SaveAsPcxAsync` | `ToPcx` |

Streams are left open and do not need to be seekable. Defaults: WebP lossy quality 75, method 4; PNG compression 6 with automatic color type; JPEG quality 75, 4:2:0; BMP automatic bit depth; GIF up to 256 colors per frame, alpha threshold 128; PCX automatic bit depth. WebP, PNG and GIF keep all animation frames; JPEG, BMP and PCX write the first frame. All option settings are in [Encoder options](encoder-options.md).

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
    var options = new DecoderOptions { TargetSize = new(w ?? 400, 0), ApplyExifOrientation = true };
    using var image = await Image.LoadAsync(Path.Combine("images", Path.GetFileName(name)), options, ct);
    return Results.File(image.ToWebP(new() { Quality = 80 }), ImageFormat.WebP.MimeType);
});
```

### Memory in a server

A decoded image holds 4 bytes per pixel and frame: a 12-megapixel photo takes 48 MB, and an animation takes that much for every frame. Decoders and encoders rent their temporary buffers (rows, planes, histograms, compressed data) from `ArrayPool<T>.Shared`, so in a steady state a request allocates about the decoded pixels plus the encoded output. The pool keeps a few buffers for reuse and releases them under memory pressure.

- For thumbnails, `DecoderOptions.TargetSize` decodes JPEG at reduced scale: a 12 MP photo shrunk to 256 px peaks at a
  few megabytes instead of about 70 MB ([Thumbnails while loading](#thumbnails-while-loading)).
- Cap untrusted input with `DecoderOptions.MaxInputBytes`, `MaxPixels` (for animations it counts all frames), `MaxFrames` and `MaxMetadataBytes`, and use `FirstFrameOnly` when a thumbnail of the first frame is enough. Pass the request's `CancellationToken` to loading, processing and saving; see [Resource limits and cancellation](limits-and-cancellation.md).
- `ToWebP()`, `ToPng()` and the other `To*` methods return an exact-size array; `Save(stream, …)` writes directly to the response stream instead.
- `MaxDegreeOfParallelism` on `DecoderOptions` and on the WebP, PNG and JPEG encoder options limits the threads one request uses (the output does not depend on it).
- Dispose images as soon as the response is written.

### Choose the output format from a MIME type

```csharp
ImageFormat target = ImageFormat.FromMimeType(request.Accept) ?? ImageFormat.Png;
image.Save(response.Body, target);
```

### WebP conversion

`WebPConverter.Convert(inputPath, outputPath, options)` preserves the `gif2webp` conversion conventions: GIF defaults to lossless and delays of 10 ms or less become 100 ms. Ordinary `Image.Load(...).SaveAsWebP(...)` preserves the original frame durations. See [Animation](animation.md).
