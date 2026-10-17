# Extending PictureSharp

Applications and plugin packages extend PictureSharp through three public points: format plugins registered in an
`ImageConfiguration`, custom frame processors, and C# 14 extension blocks that add shortcuts to `Image` and
`ImageProcessingContext`. The second half of this guide is for contributors changing the library itself.

## Format plugins

A plugin implements `IImageFormatModule` from `PictureSharp.Formats`: the format, its signature check and a decoder
and/or encoder. Create the format once with `ImageFormat.Create`; formats compare by identity.

```csharp
using PictureSharp;
using PictureSharp.Formats;

public sealed class TgaFormat : IImageFormatModule
{
    public static ImageFormat Format { get; } = ImageFormat.Create("TGA", "image/x-tga", ".tga", ".vda");

    ImageFormat IImageFormatModule.Format => Format;
    public int HeaderSize => 18;                                   // 1–4096 bytes
    public bool IsSupported(ReadOnlySpan<byte> header) => /* check the signature; header may be short */ false;
    public IImageFormatDecoder? Decoder { get; } = new TgaDecoder();
    public IImageFormatEncoder? Encoder { get; } = new TgaEncoder(); // null for read-only formats
}

public sealed record TgaEncoderOptions : EncoderOptions
{
    public override ImageFormat Format => TgaFormat.Format;
    public bool Rle { get; init; } = true;
}

sealed class TgaDecoder : IImageFormatDecoder
{
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options) =>
        new() { Format = TgaFormat.Format, Width = /* … */ 1, Height = 1 };

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        (int width, int height) = (1, 1);                         // parse the header
        context.ReservePixels(width, height);                     // fail early, before allocating
        var image = new Image(width, height);
        // … fill image.GetPixelSpan(), calling context.ThrowIfCancellationRequested() between rows
        return image;
    }
}

sealed class TgaEncoder : IImageFormatEncoder<TgaEncoderOptions>
{
    public void Encode(Image image, Stream output, TgaEncoderOptions options, CancellationToken cancellationToken)
    {
        // write image.GetPixelSpan(); the image is borrowed and the stream stays open
    }
}
```

Register the plugin once at startup, or only for the calls that need it:

```csharp
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new TgaFormat()).Build();

using var image = Image.Load("picture.tga");                      // detected from the content
image.Save("copy.tga");                                           // encoder chosen by the extension
image.Save("copy.tga", new TgaEncoderOptions { Rle = false });

var withTga = ImageConfiguration.Default.ToBuilder().AddFormat(new TgaFormat()).Build();
using var local = Image.Load(bytes, new DecoderOptions { Configuration = withTga });
local.Save("local.tga", new TgaEncoderOptions { Configuration = withTga });
```

`DecoderOptions.Configuration` and `EncoderOptions.Configuration` select the formats of a single load or save; null
means `ImageConfiguration.Default` at the time of the call. Saves that take no options (`Save(path)`,
`Save(stream, format)`) always use the default configuration.

The contract, which the library enforces:

- `AddFormat` appends after the existing formats, so built-in signatures are checked first. A format, name, MIME type or
  extension that is already registered is rejected with `ArgumentException`. `ReplaceFormat` swaps the codecs of a
  registered format (pass the registered instance, e.g. `ImageFormat.Png`) and keeps its detection position.
- The module's members are read once at registration; a built configuration is not affected by later changes to the
  module object. Decoders and encoders must be thread-safe.
- `IsSupported` may receive fewer than `HeaderSize` bytes. Index, argument, overflow and invalid-operation errors it
  throws count as "not this format".
- `Decode` should call `ReservePixels` (and `ReserveMetadata` for decompressed ICC/EXIF/XMP) before allocating. After
  decoding, the library checks `MaxPixels` and `MaxFrames` again, disposes an oversized result and sets `SourceFormat`.
  `TargetSize` and `ApplyExifOrientation` are applied to the returned image.
- Throw `ImageFormatException` for corrupt data and `NotSupportedException` for unsupported features. Index, argument,
  overflow, invalid-operation and cast errors from `Identify` and `Decode` are reported to callers as
  `ImageFormatException`.
- A container format decodes embedded images with `context.DecodeEmbedded(data)`: the same configuration's codecs,
  sharing the load's limits. The result is full size and owned by the caller.
- Encoder options derive from `EncoderOptions` and override `Format`; `IImageFormatEncoder<TOptions>` creates default
  options with `new TOptions()` and rejects options of another type.

Plugins use only public API and work under trimming and Native AOT.

### Shortcuts for a plugin

A plugin package can give its format the same shortcuts as the built-in ones:

```csharp
namespace MyCompany.PictureSharp.Tga;

public static class TgaImageExtensions
{
    extension(Image image)
    {
        public void SaveAsTga(string path, TgaEncoderOptions? options = null) => image.Save(path, options ?? new());
        public byte[] ToTga(TgaEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
```

Without options, these shortcuts save through `ImageConfiguration.Default`; with options, through
`options.Configuration` when it is set, so a plugin works without replacing the default configuration.

### Orientation, color and threads

- **Container orientation.** When a container stores rotations or mirrors outside EXIF (HEIF `irot`/`imir`, for
  example), the decoder applies them to the returned pixels and sets `Metadata.Orientation` to what is still left to
  apply, usually `ExifOrientation.TopLeft`. `DecoderOptions.ApplyExifOrientation` then rotates only by that value, so
  the image is never turned twice, and `Identify` reports the displayed width and height.
- **Color.** Put an ICC profile in `Metadata.IccProfile`. A container that describes color with code points (nclx/CICP)
  instead maps the primaries to `Metadata.Chromaticities` and a pure power transfer function to `Metadata.Gamma`;
  `Chromaticities` alone mean the sRGB curve. PQ and HLG cannot be expressed and should be reported as
  `NotSupportedException` or left undeclared. `ConvertToSrgb` and `DecoderOptions.ColorConversion` use the same
  description.
- **Threads.** Decoders, encoders, metadata codecs and gain map images may be called concurrently from several loads.
  Honor `DecoderOptions.MaxDegreeOfParallelism` or the encoder's own setting; the output must not depend on it.

### HDR gain maps

A decoder that finds an HDR gain map attaches it, so `Image.Hdr`, `HasHdr`, `ToHdr` and `ToneMap` work as for JPEG
files. Only do so when `context.Options.LoadGainMap` is set; the context ignores the call otherwise.

```csharp
sealed class ContainerGainMap(byte[] mapData, Size size) : GainMapImage(size)
{
    // Called by ToHdr or the first geometric edit, possibly on another thread after the load returned.
    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
        Image.Load(mapData, new DecoderOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });
}

// In IImageFormatDecoder.Decode, after decoding the base image:
if (context.Options.LoadGainMap)
{
    try
    {
        GainMapMetadata metadata = GainMapMetadata.ParseIso(isoPayload, mapChannels: 1, alternateIcc);
        context.AttachGainMap(metadata, new ContainerGainMap(mapBytes, mapSize));
    }
    catch (Exception ex) when (ex is ImageFormatException or NotSupportedException)
    {
        context.DeclareGainMap(HdrGainMapKinds.Iso21496, ex.Message, corrupt: ex is ImageFormatException);
    }
}
```

- When the file describes the same map in several models (iPhones since iOS 18 store ISO 21496-1 and Apple metadata),
  attach the parsed model with `AttachGainMap(metadata, map, detected)`, where `detected` lists every model and must
  include `metadata.Kind`; `Image.Hdr.Detected` then matches what a JPEG reports (0.7.1).
- `GainMapMetadata.ParseIso` reads the ISO 21496-1 payload (from the minimum version on) and `ParseApple` the Apple
  model (EXIF MakerNote headroom and the map's XMP). In `Identify`, report `ImageInfo.Hdr = metadata.ToHdrInfo(mapSize)`
  (with `Detected` set to the same models) when `LoadGainMap` is set.
- The map's pixels count toward `MaxPixels`; a map over the limit, unparsable metadata or a base color other than sRGB
  or Display P3 is reported through `Image.Hdr` and never fails the load.
- The base image's color comes from the returned image's ICC profile or `Chromaticities`/`Gamma`, so keep them when
  attaching a map, also with `IgnoreMetadata`.
- The library carries the map through `TargetSize`, `ApplyExifOrientation`, `ColorConversion` and geometric edits.
  `GainMapImage.Decode` returns the map at its stored size; it must own its data and must not use the decode context.
- Saving a plugin's gain map into JPEG (`JpegGainMapMode.Preserve`/`Require`) is not supported yet: `Preserve` writes
  SDR and `Require` throws `NotSupportedException`.

### Metadata without pixels

`IImageFormatModule.MetadataCodec` enables `Image.ReadMetadata` and `Image.EditMetadata` for the format.
`IImageFormatMetadataCodec.Read` returns the ICC, EXIF and XMP blocks, charging them with `context.ReserveMetadata`;
`Rewrite` returns a copy of the file with the edited blocks and by default throws `NotSupportedException`, so a
read-only implementation needs only `Read`. The library checks limits and cancellation, compares the edit with the
original (an unchanged edit writes nothing new) and writes files atomically.

### SPI compatibility

The plugin interfaces change only additively within a minor version: new members get default implementations, and new
types or optional parameters are added beside the existing ones. A breaking change happens only in a new minor version,
with a migration note in the release notes and the API snapshot updated.

## Custom frame processors

`IFrameProcessor` (`PictureSharp.Processing`) produces a new buffer for every frame and may change the canvas size:

```csharp
using System.Drawing;
using PictureSharp;
using PictureSharp.Processing;

public readonly struct LeftHalf : IFrameProcessor
{
    public Size GetOutputSize(Size source) => new(Math.Max(1, source.Width / 2), source.Height);

    public void Process(ReadOnlySpan<Rgba32> source, Size sourceSize, Span<Rgba32> destination, Size destinationSize,
        CancellationToken cancellationToken)
    {
        for (int y = 0; y < destinationSize.Height; y++)
            source.Slice(y * sourceSize.Width, destinationSize.Width).CopyTo(destination[(y * destinationSize.Width)..]);
    }
}

public static class MyOperations
{
    extension(ImageProcessingContext x)
    {
        public ImageProcessingContext LeftHalf() => x.ApplyProcessor(new LeftHalf());
    }
}

image.Mutate(x => x.AutoOrient().LeftHalf().Resize(800, 0));
```

- `GetOutputSize` is asked once per operation because all frames share one canvas; both sides must be positive.
- `Process` receives row-major, straight-alpha pixels. The destination starts transparent black; do not keep the spans.
- All frames are prepared before any is published. If `Process` throws or the operation is canceled, the image keeps its
  previous frames. Within `Clone` the source is read without being copied.
- A struct processor runs without boxing. In-place edits that keep the size are simpler with
  `Apply(Action<ImageFrame>)`; see [Image processing](image-processing.md#custom-operations).

## Contributing to the library

### Add a built-in format

Add its immutable descriptor to Domain and public format options to Application under the functional format namespace.
Implement detection, identification, canonical decoding (`DecodedImage`) and encoding in an Infrastructure module. Keep
codec-specific timing, binary headers and metadata selection there. Add a `FormatRegistration` only in
`Composition/CodecRegistry.cs`; `CodecResolver` builds the codecs per `ImageConfiguration`, so a container codec
receives the resolver of the configuration being built. Public shortcuts call the `Image` extension members. Built-in
codecs use the internal contracts (`IImageDecoder` returns a `DecodedImage` with stored size and reduction scale); the
public plugin SPI is adapted to them in `Application/Extensibility`.

Validate stored dimensions and frame counts before allocating raster buffers. Charge shared `DecodeContext.Budget` at
the leaf decoder; embedded formats receive a child context, avoiding double charging the same canvas. Preserve stored
dimensions and reduction scale in nested `DecodedImage` results. Charge metadata after decompression; never use mutable
execution counters in options or codec singletons.

Extend `CodecContractTests` and synthetic corpus/Oracle coverage for the format. Test malformed and truncated input,
cancellation, shared limits and stream ownership. Extend the AOT smoke consumer and benchmark matrix. Do not normalize
I/O failures into corrupt-image exceptions.

### Add a built-in processing operation

Keep numeric work in Algorithms and orchestration in Application. For an operation producing buffers, use
`ProcessingState.ReplaceFrames` with a static transform and explicit state. Return a `FrameReplacement` for each frame;
the image aggregate validates and commits the whole batch. Do not call frame publication or ownership methods directly.
For in-place work use writable frames so copy-on-write is honored, and retain row/frame cancellation checks.

Test known pixel vectors, multi-frame canvas consistency, cancellation after preparing the first frame and source
independence for `Clone`. Add a representative benchmark; preserve scalar/SIMD equality and allocation-free hot loops.

### Change the model or public API

Put required validation on the model operation, including internal creation paths. Keep `Size`, `Rectangle` and
`TimeSpan` at public boundaries. Prefer `field` for simple validated properties; use readonly internal values for shared
rules. Extension blocks group facade operations, while invariants remain instance methods. Do not convert mutable images
to records or add abstraction layers without a concrete caller.

Update XML docs, the API snapshot and the guides. Test public calls with arrays, spans and memory through the C# 14
consumer compilation test. Run Release, Oracle, scalar fallbacks and `bash scripts/verify-package-consumer.sh` before
declaring completion. This script packs locally and verifies an isolated package consumer with full trimming and Native
AOT on linux-x64.

CI runs managed tests, pinned reference tools and the package consumer on pushes and pull requests; release publishing
depends on the same validation workflow. Benchmarks remain a controlled local comparison on the same machine. ARM64
needs a separate hardware run and is not implied by x64 success.
