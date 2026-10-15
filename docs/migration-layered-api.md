# Migration from 0.5.4 to the 0.5.5 layered API

Version 0.5.5 introduces breaking changes from 0.5.4. The package name and model remain `PictureSharp`; old operation members on `Image` are removed. Import the functional namespaces and migrate calls as shown below.

## Imports

```csharp
using PictureSharp;
using PictureSharp.Formats.Png;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;

```

Add `PictureSharp.Formats.<Format>` for each format's options, enums and shortcuts. `DecoderOptions`, `EncoderOptions` and `ImageInfo` are in `PictureSharp.IO`. Processing contexts, modes and resize options are in `PictureSharp.Processing`. Model types including `Image`, `ImageFrame`, `ImageMetadata`, `Rgba32`, `PixelFormat`, `ImageFormat`, `ExifOrientation` and `MetadataKinds` remain in `PictureSharp`.

## Before → after

| Before | After |
| --- | --- |
| `Image.Load(data, options)` | `ImageReader.Load(data, options)` |
| `Image.LoadAsync(path, options, ct)` | `ImageReader.LoadAsync(path, options, ct)` |
| `Image.Identify(data)` | `ImageReader.Identify(data)` |
| `Image.IdentifyAsync(stream, ct)` | `ImageReader.IdentifyAsync(stream, ct)` |
| `Image.DetectFormat(data)` | `ImageFormats.DetectFormat(data)` |
| `image.Save(path)` | `ImageWriter.Save(image, path)` |
| `image.Save(stream, options, ct)` | `ImageWriter.Save(image, stream, options, ct)` |
| `image.SaveAsync(path, options, ct)` | `ImageWriter.SaveAsync(image, path, options, ct)` |
| `image.Encode(format)` | `ImageWriter.Encode(image, format)` |
| `image.Encode(options, ct)` | `ImageWriter.Encode(image, options, ct)` |
| `ImageFormat.All` | `ImageFormats.All` |
| `ImageFormat.FromExtension(path)` | `ImageFormats.FromExtension(path)` |
| `ImageFormat.FromMimeType(mime)` | `ImageFormats.FromMimeType(mime)` |
| `format.CanDecode` / `format.CanEncode` | `ImageFormats.CanDecode(format)` / `ImageFormats.CanEncode(format)` |

`Mutate` and `Clone(operation)` retain their call syntax after adding `using PictureSharp.Processing`; they are extension methods. `Clone()` remains a model method. Format-specific `SaveAs*`, `SaveAs*Async` and `To*` also retain their syntax after importing the corresponding format namespace. `IcoFile`, ICO collections/options and `WebPConverter` move to their format namespace.

```csharp
using PictureSharp;
using PictureSharp.Formats.WebP;
using PictureSharp.IO;
using PictureSharp.Processing;

using Image image = await ImageReader.LoadAsync("photo.jpg");
image.Mutate(x => x.AutoOrient().Resize(1600, 0).UnsharpMask());
await image.SaveAsWebPAsync("photo.webp", new() { Quality = 82 });
using Image thumbnail = image.Clone(x => x.Resize(256, 256, ResizeMode.Crop));
ImageWriter.Save(thumbnail, "thumbnail.png");
```

## Behavior and verification

Existing pixel results, codec options, thumbnail sizing, exceptions, cancellation, metadata and disposal rules are preserved. Input streams remain open and are read from their current position. Output stream writes can be partial when canceled; file saves encode before writing. Async methods perform asynchronous I/O and synchronous CPU work, as before.

An ICO collection owns its loaded images; saving borrows the supplied images. Mutating a borrowed variant and disposing a collection retain the existing behavior.

`DocsExamplesTests`, the approved API snapshot and regression tests cover the migrated entrypoints. `samples/PictureSharp.AotSmoke` exercises all formats, processing, stream I/O, raw pixel round trips, ICO variants, animation and reduced JPEG decoding under trimming and Native AOT.

## Domain and option changes

| Before | After |
| --- | --- |
| `new DecoderOptions { StrictChecksums = true }` | `new DecoderOptions { Png = new() { StrictChecksums = true } }` |
| `WebPFancyUpsampling` / `WebPBypassFiltering` | `WebP.FancyUpsampling` / `WebP.BypassFiltering` |
| `IcoIndex` / `IcoPreferredSize` | `Ico.Index` / `Ico.PreferredSize` |
| Reusing an attached `ImageFrame` in another image | `destination.Frames.AddClone(frame)` |
| Mutating an array returned by `Metadata.Exif`/`Xmp`/`IccProfile` | Edit a snapshot and reassign, or call `SetBlock` |
| Reading block length through an array getter | `Metadata.GetBlockLength(kind)` |

Negative `ImageFrame.Duration` now fails at assignment, including animation construction. Zero remains valid. Format-specific maximum durations still fail in the encoder. Image state and collection access after `Dispose` consistently throw; previously returned views cannot be revoked.

Geometry and spatial filters prepare every frame before committing the common canvas. Cancellation or preparation failure leaves that operation's original frame buffers unchanged. Earlier successful operations in the same `Mutate` call stay applied; in-place color operations can remain partially applied after cancellation.
