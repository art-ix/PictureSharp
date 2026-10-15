# PictureSharp 0.5.6

**Breaking API changes from 0.5.5.** `Image` is again the single entry point, and the library gains public extension points. The image model and its domain rules are unchanged.

### API

- Load, identify and save through `Image`, in the style of ImageSharp: `Image.Load`, `LoadAsync`, `Identify`, `IdentifyAsync`, `DetectFormat`, `image.Save`, `SaveAsync`, `Encode`. These are C# 14 extension members declared outside the model, with the same overloads as before (byte arrays still bind to the non-copying `ReadOnlyMemory<byte>` overloads).
- `ImageFormat.All`, `FromExtension`, `FromMimeType` and the properties `format.CanDecode` / `CanEncode` replace `ImageFormats`.
- `ImageReader`, `ImageWriter`, `ImageFormats` and the `PictureSharp.IO` namespace are removed. `DecoderOptions`, `EncoderOptions`, `ImageInfo` and `Mutate`/`Clone` move to `PictureSharp`, so one `using PictureSharp;` covers loading, processing and saving.
- In projects with `ImplicitUsings`, the package adds `global using PictureSharp;`. Opt out with `PictureSharpImplicitUsings=false`; Windows Forms, WPF and MAUI projects are opted out by default.

### Extensibility

- `ImageConfiguration`: an immutable, thread-safe set of formats built with `ToBuilder()` → `KeepOnly`, `RemoveFormat`, `AddFormat`, `ReplaceFormat`. `ImageConfiguration.Default` can be replaced at startup; `DecoderOptions.Configuration` selects a configuration for one load, e.g. only PNG and JPEG for untrusted uploads.
- Format plugins in `PictureSharp.Formats`: `IImageFormatModule`, `IImageFormatDecoder`, `IImageFormatEncoder<TOptions>` and `ImageDecodeContext` (shared limits, cancellation, decoding of embedded images). `ImageFormat.Create` defines new formats; `EncoderOptions` can be derived for plugin options. Plugin results get the same limits, error normalization, thumbnails and orientation as built-in formats, and a failing signature check cannot escape detection.
- Custom frame processors: `IFrameProcessor` with `ApplyProcessor<T>` produce new, possibly resized, buffers for every frame and publish them atomically; struct processors run without boxing.
- A BMP with embedded PNG/JPEG data now throws `NotSupportedException` when the configuration lacks that format.

See [Extending PictureSharp](https://github.com/art-ix/PictureSharp/blob/v0.5.6/docs/extending-picturesharp.md) and [Loading and saving](https://github.com/art-ix/PictureSharp/blob/v0.5.6/docs/loading-and-saving.md).

### Validation

Local verification: Release build with zero warnings/errors, **5167/5167 tests**, **150/150 Oracle**, **113/113 scalar fallback tests**. The local package passed full trimming and Native AOT consumers on linux-x64, including a format plugin, a frame processor and the package's global using. Codec and processing algorithms are unchanged; the new code runs outside pixel loops, so no benchmark run was made for this release.

ARM64 hardware verification was not performed.
