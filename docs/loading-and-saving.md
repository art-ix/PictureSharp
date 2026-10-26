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
| `Image.Load(Stream stream, DecoderOptions? options = null)` | Reads from the current position to the end; the stream stays open. |
| `Image.Load(ReadOnlyMemory<byte> data, ...)` | A `byte[]` binds here and is not copied; keep it unchanged during decoding. |
| `Image.Load(ReadOnlySpan<byte> data, ...)` | The data is copied first. |
| `Image.LoadAsync(string path \| Stream stream [, DecoderOptions? options], CancellationToken ct = default)` | Asynchronous I/O, then decoding; the token also stops decoding. |
| `Image.Load(..., DecoderOptions? options, CancellationToken ct)` | Every source has a cancelable overload; see [Limits and cancellation](limits-and-cancellation.md). |
| `Image.LoadPixelData(...)` | An image from raw pixels; see [Pixels and interop](pixels-and-interop.md). |
| `new Image(width, height [, background])` | A blank image, transparent unless a background is given. |

The format is detected from the content, so a PNG named `photo.jpg` loads fine. `image.SourceFormat` tells which format
was decoded (`null` for images created in memory).

### Supported formats

| Format | Read | Write |
| --- | --- | --- |
| JPEG | baseline, progressive, CMYK/YCCK, HDR gain maps (ISO 21496-1, Apple) | baseline, progressive, 4:4:4–4:2:0, ISO 21496-1 gain maps |
| PNG | all color types and bit depths, APNG | gray, RGB(A), palette, Adam7, APNG |
| WebP | lossy, lossless, alpha, animation | lossy, lossless, near-lossless, alpha, animation |
| GIF | GIF87a/89a, interlace, transparency, animation | exact or quantized palettes, dithering, animation |
| BMP | palette, RLE, bit fields, alpha, embedded PNG/JPEG | 1/4/8/24/32-bit |
| ICO | PNG and DIB 1/4/8/24/32-bit icons, multiple sizes | PNG or 32-bit DIB icons, multiple sizes |
| PCX | 1–8-bit, 24-bit RGB, 32-bit RGBA | 1/4/8/24/32-bit, RLE |

HEIC/HEIF (HEVC 8/10-bit, tile grids, alpha, HDR gain maps; writing 8-bit 4:2:0) comes from the
[PictureSharp.Heif](https://github.com/art-ix/PictureSharp.Heif) plugin. Encoder settings: [Encoder options](encoder-options.md).

### PCX support

PCX supports monochrome, packed and planar palette images, grayscale, RGB and RGBA. DCX and legacy color images below 8
bits per plane without palette information (versions 0 and 3) are not supported. See
[PCX encoder options](encoder-options.md#pcx).

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
| `MaxInputBytes` | `Array.MaxLength` | Maximum encoded input size. |
| `MaxFrames` | 65536 | Maximum stored animation frames. |
| `MaxMetadataBytes` | 64 MiB | Maximum total ICC, EXIF and XMP size after decompression. |
| `MaxPixels` | 2^28 | Maximum width × height before allocation; for animations, all decoded frames together. |
| `FirstFrameOnly` | false | Decode only the first frame of animated WebP/APNG/GIF. |
| `ApplyExifOrientation` | false | Same as `Mutate(x => x.AutoOrient())` after loading. |
| `TargetSize` | null | Shrink to fit inside this size while loading; see [below](#thumbnails-while-loading). |
| `TargetResampling` | `Lanczos3` | Filter of the resize to `TargetSize`. |
| `TargetReducingGap` | 2 | JPEG: how much larger than the final size a reduced decode must stay, per side (at least 1). |
| `IgnoreMetadata` | false | Do not read ICC, EXIF and XMP blocks. |
| `Png.StrictChecksums` | false | PNG: fail on CRC mismatches instead of ignoring them. |
| `Jpeg.CmykStorage` | `AdobeInverted` | JPEG with four components: `Plain` reads the values as ink amounts (as TIFF stores CMYK) instead of Adobe's inverted ones. |
| `WebP.FancyUpsampling` | true | WebP lossy: smooth chroma upsampling, as libwebp. |
| `WebP.BypassFiltering` | false | WebP lossy: skip the deblocking filter (faster, lower quality). |
| `ColorConversion` | `None` | `ToSrgb` converts to sRGB; see [Color management](color-management.md). |
| `LoadGainMap` | false | Attach the HDR gain map; see [HDR photos](hdr.md). |
| `Ico.PreferredSize`, `Ico.Index` | null | Which image of an ICO file to load; see [ICO icons](ico.md). |
| `Configuration` | null | The formats this load may use; see [below](#choosing-the-formats-imageconfiguration). |
| `FormatOptions` | empty | Settings of plugin formats, e.g. which page to load; see [Extending PictureSharp](extending-picturesharp.md#decoder-options). |
| `MaxDegreeOfParallelism` | -1 | Threads; -1 is automatic. Pixels do not depend on it. |

`DecoderOptions.Default` holds the defaults; options are records, so `DecoderOptions.Default with { MaxPixels =
1_000_000 }` creates a variant.

### Thumbnails while loading

`TargetSize` loads an image already shrunk to fit inside a box, with the size `Thumbnail(width, height)` would give: 0
means no limit on that side, and an image that already fits is never enlarged.

```csharp
var options = new DecoderOptions { TargetSize = new(256, 256), ApplyExifOrientation = true };
using var thumbnail = Image.Load("photo.jpg", options);   // at most 256×256, upright
```

- **JPEG** is decoded at 1/2, 1/4 or 1/8 scale by a reduced inverse DCT (bit-exact with libjpeg-turbo's
  `scale_denom`), then resampled, without ever building full-size planes or RGBA; see
  [Performance](performance.md#jpeg-thumbnails-in-054). The scale is the largest that keeps `TargetReducingGap` times
  the final size on both axes (2 by default, as Pillow and libvips); 1 saves the most. All JPEG variants are supported.
- **Progressive JPEG** still keeps the full-size coefficients (2 bytes per sample, 36 MB for a 12 MP 4:2:0 photo); only
  the planes and pixels shrink.
- **Other formats** are decoded in full and then resized with `Thumbnail`, with no memory saving. Every animation frame
  is resized.
- **Orientation.** With `ApplyExifOrientation` the box applies to the upright image.
- **Pixels differ slightly from `Load` + `Thumbnail`** (53–56 dB PSNR on 12–27 MP photos with the default gap, 39–53 dB
  with gap 1); the size is the same. When the scale works out to 1, the result is identical.
- **Limits** and `Identify` use the stored size, so `MaxPixels` rejects a huge JPEG even for a tiny target.

## Inspecting without decoding

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;

ImageInfo info = Image.Identify("photo.webp");                 // also Stream, ReadOnlyMemory<byte>, ReadOnlySpan<byte>; IdentifyAsync
Console.WriteLine($"{info.Format.Name} {info.Width}x{info.Height}, {info.Color.Model}, alpha: {info.Color.HasAlpha}");

ImageFormat? format = Image.DetectFormat("unknown.bin");       // reads only the header; also Stream and ReadOnlySpan<byte>
if (format == ImageFormat.Png) { /* ... */ }
```

`Identify(ReadOnlyMemory<byte>)` (and `byte[]`) does not copy the input; the span overload does. `ImageInfo` also
reports `FrameCount` (null when only decoding could tell), `IsAnimated`, `Compression` (lossless, lossy, mixed
or unknown), `Orientation`, the declared `Resolution` (null when the file has none) and the gain map (`GainMap`).

`info.Color` describes the stored samples as the format defines them after its own colour transform, so a YCbCr JPEG is
`Rgb` and a YCCK JPEG `Cmyk`:

| Member | Meaning |
| --- | --- |
| `Model` | `Gray`, `Rgb`, `Palette`, `Cmyk`, `Lab` or `Other` |
| `ChannelCount` | Samples per pixel, alpha and extra channels included; a palette index counts as one |
| `BitsPerSample`, `SampleFormat` | Depth of one sample (the index depth for palettes) and its type (unsigned, signed, float) |
| `AlphaMode`, `HasAlpha` | `None`, `Straight` (also binary transparency) or `Premultiplied` |
| `HasIccProfile` | Whether an ICC profile is embedded; it is not read or validated |
| `Cicp` | H.273 code points from PNG cICP or sRGB, BMP sRGB or HEIF nclx; `IsHdrTransfer` for PQ and HLG |

These members are filled with `IgnoreMetadata` too, and copy no metadata.
`DetectFormat(Stream)` rewinds seekable streams.

`ImageFormat` has `Name`, `MimeType`, `FileExtensions`, `CanDecode` and `CanEncode`. Look formats up with
`ImageFormat.FromExtension("photo.JPG")` or `ImageFormat.FromMimeType("image/webp")`; `ImageFormat.All` lists them.
These lookups and `DetectFormat` use `ImageConfiguration.Default`.

## Saving

```csharp
using PictureSharp;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;

image.Save("out.webp");                                        // format from the extension, default options
image.Save("out.bin", new PngEncoderOptions());                // the options decide the format
image.Save(stream, ImageFormat.Jpeg);                          // default JPEG options
image.Save(stream, new WebPEncoderOptions { Lossless = true });
await image.SaveAsync("out.png", cancellationToken);
```

Each format also has methods taking a path or stream and optional options (`null` means the defaults):

```csharp
using PictureSharp;
using PictureSharp.Formats.Bmp;
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;

image.SaveAsWebP("photo.webp", new() { Quality = 82 });
image.SaveAsPng(stream);
await image.SaveAsJpegAsync("photo.jpg", new() { Quality = 90, Progressive = true });
byte[] webp = image.ToWebP();
byte[] encoded = image.Encode(ImageFormat.Png);                // runtime format, default options
byte[] custom = image.Encode(new PngEncoderOptions { CompressionLevel = 9 });
```

The shortcuts exist for every format: `SaveAs<Format>`, `SaveAs<Format>Async` and `To<Format>` (WebP, PNG, JPEG, BMP,
GIF, PCX).

Every encoder options type has `Metadata` (which blocks to write) and `Configuration` (null means
`ImageConfiguration.Default`). Streams stay open and need not be seekable. WebP, PNG and GIF keep all animation frames;
JPEG, BMP and PCX write the first. Defaults and settings are in [Encoder options](encoder-options.md).

## Choosing the formats: `ImageConfiguration`

An `ImageConfiguration` lists the formats that loading, identification and saving may use. It is immutable, so one
instance can be shared across threads; derive another with `ToBuilder()`.

```csharp
using PictureSharp;

// Accept only PNG and JPEG from untrusted uploads; other formats fail as unrecognized.
ImageConfiguration uploads = ImageConfiguration.Default.ToBuilder()
    .KeepOnly(ImageFormat.Png, ImageFormat.Jpeg)
    .Build();
using var upload = Image.Load(stream, new DecoderOptions { Configuration = uploads, MaxPixels = 50_000_000 });

// Or change what the whole application accepts, once at startup.
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder()
    .RemoveFormat(ImageFormat.Pcx)
    .Build();
```

| Member | Notes |
| --- | --- |
| `ImageConfiguration.Default` | Used whenever no configuration is given, including `Save(path)`, `DetectFormat` and the `ImageFormat` lookups. Initially the built-in formats. Replacing it publishes a complete snapshot; running calls keep the one they started with. |
| `DecoderOptions.Configuration`, `EncoderOptions.Configuration` | The configuration of one call; `null` means `Default` at the time of the call. |
| `Formats`, `CanDecode`, `CanEncode`, `FromExtension`, `FromMimeType` | The `ImageFormat` lookups, for this configuration. |
| `ToBuilder()` → `KeepOnly`, `RemoveFormat`, `AddFormat`, `ReplaceFormat`, `Build` | `KeepOnly` keeps the detection order. `AddFormat` and `ReplaceFormat` take plugins; see [Extending PictureSharp](extending-picturesharp.md). |

A BMP embedding PNG or JPEG data needs that format in the configuration, otherwise the load throws
`NotSupportedException`. `IcoFile` always uses the built-in ICO codecs.

## Recipes

### Convert a folder to WebP

```csharp
using PictureSharp.Formats.WebP;

foreach (string path in Directory.EnumerateFiles("photos"))
{
    if (ImageFormat.FromExtension(path) is not { } format || !format.CanDecode)
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
using PictureSharp;
using PictureSharp.Formats.WebP;

app.MapGet("/thumb/{name}", async (string name, int? w, CancellationToken ct) =>
{
    var options = new DecoderOptions { TargetSize = new(w ?? 400, 0), ApplyExifOrientation = true };
    using var image = await Image.LoadAsync(Path.Combine("images", Path.GetFileName(name)), options, ct);
    return Results.File(image.ToWebP(new() { Quality = 80 }), ImageFormat.WebP.MimeType);
});
```

The `PictureSharp.AspNetCore` package offers `Results.Extensions.Image(image)`, which negotiates the format from
`Accept`, writes the caching headers and stops on `RequestAborted`; see [Adapters](adapters.md#aspnet-core).

### Memory in a server

A decoded image holds 4 bytes per pixel and frame (48 MB for a 12 MP photo); codecs rent temporary buffers from
`ArrayPool<T>.Shared`. For thumbnails, `TargetSize` keeps a 12 MP JPEG at a few megabytes instead of about 70 MB. Cap
untrusted input with the limits, pass the request's `CancellationToken` ([Limits and
cancellation](limits-and-cancellation.md)), write with `Save(stream, …)` instead of `To*`, limit threads with
`MaxDegreeOfParallelism`, and dispose images as soon as the response is written.

### WebP conversion

`WebPConverter.Convert(inputPath, outputPath, options)` follows the `gif2webp` conventions: GIF defaults to lossless and
delays of 10 ms or less become 100 ms. `Image.Load(...).SaveAsWebP(...)` keeps the original durations. See
[Animation](animation.md).
