# Extending PictureSharp

Applications extend PictureSharp with format plugins, custom frame processors and C# 14 extension blocks; plugins use
only public API and work under trimming and Native AOT. The last section is for library contributors.

## Format plugins

A plugin implements `IImageFormatModule` (`PictureSharp.Formats`): the format, its signature check and a decoder and/or
encoder. Create the format once with `ImageFormat.Create`; formats compare by identity.

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

Saves without options (`Save(path)`, `Save(stream, format)`) always use the default configuration. The library enforces
this contract:

- `AddFormat` appends after existing formats, so built-in signatures are checked first; a duplicate format, name, MIME
  type or extension throws `ArgumentException`. `ReplaceFormat` swaps the codecs of a registered format (e.g.
  `ImageFormat.Png`) and keeps its detection position.
- Module members are read once at registration. Codecs must be thread-safe and their output must not depend on
  `MaxDegreeOfParallelism`.
- `IsSupported` may receive fewer than `HeaderSize` bytes; index, argument, overflow and invalid-operation errors count
  as "not this format".
- `Decode` should call `ReservePixels` (and `ReserveMetadata` for decompressed ICC/EXIF/XMP) before allocating. The
  library rechecks `MaxPixels` and `MaxFrames`, sets `SourceFormat` and applies `TargetSize` and
  `ApplyExifOrientation`.
- Throw `ImageFormatException` for corrupt data and `NotSupportedException` for unsupported features; index, argument,
  overflow, invalid-operation and cast errors are reported as `ImageFormatException`.
- A container decodes embedded images with `context.DecodeEmbedded(data)`, using the same codecs and shared limits.
- A format whose image is stored as strips or tiles in another format (JPEG tiles in TIFF) reserves the image once and
  decodes each part with `context.DecodeEmbeddedPart(data, width, height)`. A part is not charged against the reserved
  image and is not a frame: it may decode to at most `width` x `height` pixels (itself at most `MaxPixels`), as
  stored, without EXIF orientation, gain map, color conversion or metadata. Dispose each part before decoding the next,
  so a load holds at most the image and one part. The overload with `EmbeddedPartOptions` replaces settings for the
  part only: `Jpeg` (for example `CmykStorage = JpegCmykStorage.Plain` for the CMYK strips of a TIFF file) and
  `MaxDegreeOfParallelism` (for example 1 when the container decodes several parts at once); a null setting keeps the
  load's own:

  ```csharp
  context.ReservePixels(width, height);
  var image = new Image(width, height);
  foreach ((ReadOnlyMemory<byte> data, int x, int y) in tiles)
  {
      using Image tile = context.DecodeEmbeddedPart(data, tileWidth, tileHeight);
      // Copy the part of the tile inside the image; check that the tile covers it.
  }

  var cmyk = new EmbeddedPartOptions { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain } };
  using Image strip = context.DecodeEmbeddedPart(data, stripWidth, stripHeight, cmyk);
  ```
- Encoder options derive from `EncoderOptions` and override `Format`; `IImageFormatEncoder<TOptions>` creates defaults
  with `new TOptions()` and rejects options of another type.

### Shortcuts for a plugin

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

With options set, they save through `options.Configuration`, so the default configuration need not change.

### Orientation and color

- **Container orientation.** Apply rotations stored outside EXIF (HEIF `irot`/`imir`) to the pixels and set
  `Metadata.Orientation` to what remains (usually `TopLeft`); `Identify` reports the displayed size.
- **Color.** Put an ICC profile in `Metadata.IccProfile`, or map nclx/CICP primaries to `Metadata.Chromaticities` and a
  pure power transfer to `Metadata.Gamma` (`Chromaticities` alone mean the sRGB curve). PQ and HLG cannot be expressed:
  throw `NotSupportedException` or leave them undeclared.

### HDR gain maps

When `context.Options.LoadGainMap` is set, a decoder that finds a gain map attaches it, so `Image.Hdr`, `ToHdr` and
`ToneMap` work as for JPEG.

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

- When a file stores the map in several models (iPhones since iOS 18 store ISO 21496-1 and Apple metadata), call
  `AttachGainMap(metadata, map, detected)` with every model in `detected` (including `metadata.Kind`), so
  `Image.Hdr.Detected` matches a JPEG (0.7.1).
- `GainMapMetadata.ParseIso` reads ISO 21496-1 payloads, `ParseApple` the Apple model. In `Identify`, report
  `ImageInfo.Hdr = metadata.ToHdrInfo(mapSize)` (with the same `Detected`) when `LoadGainMap` is set.
- Map pixels count toward `MaxPixels`. An oversized map, unparsable metadata or a base color other than sRGB or Display
  P3 is reported through `Image.Hdr` and never fails the load.
- Keep the base image's ICC profile or `Chromaticities`/`Gamma` when attaching a map, also with `IgnoreMetadata`.
- The library carries the map through `TargetSize`, orientation, color conversion and geometric edits.
  `GainMapImage.Decode` returns the map at its stored size, owns its data and must not use the decode context.
- Saving as JPEG writes a plugin's gain map like an edited JPEG map, with metadata from `ToIso()` (0.8.0).

An encoder writes the attached map with `image.GetGainMap()` (namespace `PictureSharp.Formats`, 0.8.0):

```csharp
// In IImageFormatEncoder.Encode; require: true throws like JpegGainMapMode.Require instead of returning null.
if (image.GetGainMap(require: options.GainMap == MyGainMapMode.Require) is { } gainMap)
{
    byte[] iso = gainMap.Metadata.ToIso();   // the ISO 21496-1 payload, as ParseIso reads it
    if (gainMap.Map is ContainerGainMap own)
    {
        // Unedited and unrotated since this plugin attached it: copy the compressed map as is.
    }
    else
    {
        using Image map = gainMap.Map.Decode(options.MaxDegreeOfParallelism, cancellationToken);
        // Encode the map: the size and orientation of the image's pixels; a gray map has R = G = B.
    }
}
```

- Only ISO 21496-1 maps are returned; a map with Apple metadata only gives null (`NotSupportedException` when
  required).
- `ToIso()` stores the multiplication color space only as a flag: when `Metadata.MultiplicationSpace` is not null,
  store those primaries with the map (an ICC profile or a `colr` box).
- `GetGainMap` decodes nothing; `Map.Decode` returns a new image on every call.

### Metadata without pixels

`IImageFormatModule.MetadataCodec` enables `Image.ReadMetadata` and `Image.EditMetadata`. `Read` returns the ICC, EXIF
and XMP blocks (charged with `context.ReserveMetadata`); `Rewrite` returns the file with edited blocks and throws
`NotSupportedException` by default. The library checks limits and cancellation, skips unchanged edits and writes files
atomically.

### Decoder options

A plugin that lets the caller choose what to decode (a TIFF page, a DDS mip level, a HEIF item) derives its settings
from `FormatDecoderOptions`. Callers pass them in `DecoderOptions.FormatOptions`, and the plugin reads its own record
with `GetFormatOptions<T>()` (0.7.6):

```csharp
public sealed record TgaDecoderOptions : FormatDecoderOptions
{
    public int Page { get; init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Page)); }
}

// In the plugin: the same selection in Identify, Decode and the metadata codec.
public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
{
    int page = options.GetFormatOptions<TgaDecoderOptions>()?.Page ?? 0;
    // ... describe that page
}

public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
{
    int page = context.Options.GetFormatOptions<TgaDecoderOptions>()?.Page ?? 0;
    // ... ReservePixels and decode that page
}

// In the application:
using var image = Image.Load(path, new DecoderOptions { FormatOptions = [new TgaDecoderOptions { Page = 2 }] });
```

- Without its record a plugin decodes what it did before. It ignores the records of other plugins, and built-in
  formats ignore them all, so one `DecoderOptions` can carry settings for several plugins.
- `FormatOptions` holds at most one record per type and is copied when set; null entries and duplicate types throw
  `ArgumentException`.
- Check values in the record (`ArgumentOutOfRangeException`); throw `ImageFormatException` when the selection is not in
  the file, as `Ico.Index` does. `ContainerItemCount` still counts every item.
- Limits, cancellation and `ReservePixels` apply to the selected item. `DecodeEmbedded` passes the same options to the
  embedded image; `DecodeEmbeddedPart` passes the limits and format options without orientation, gain map, color
  conversion or metadata, with the `EmbeddedPartOptions` of its overload replacing `Jpeg` and `MaxDegreeOfParallelism`. Frame readers (`OpenFrameReaderAsync`) do not support plugin formats.

### SPI compatibility

Plugin interfaces change only additively within a minor version (default interface members, new types or optional
parameters). Breaking changes wait for a new minor version and come with a migration note.

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

- `GetOutputSize` is asked once per operation (all frames share one canvas); both sides must be positive.
- `Process` receives row-major straight-alpha pixels and a transparent black destination; do not keep the spans.
- All frames are prepared before any is published, so an exception or cancellation keeps the previous frames. Within
  `Clone` the source is not copied.
- A struct processor runs without boxing. Same-size in-place edits are simpler with `Apply(Action<ImageFrame>)`; see
  [Image processing](image-processing.md#custom-operations).

## Contributing to the library

**A built-in format.** Put its descriptor in Domain, public options in Application under the format namespace, and
detection, identification, decoding (`DecodedImage`) and encoding in an Infrastructure module. Register it only as a
`FormatRegistration` in `Composition/CodecRegistry.cs`; `CodecResolver` builds codecs per `ImageConfiguration`. Built-in
codecs use the internal contracts; the plugin SPI is adapted to them in `Application/Extensibility`. Validate stored
dimensions and frame counts before allocating, charge `DecodeContext.Budget` at the leaf decoder (embedded formats get a
child context), charge metadata after decompression, and keep execution counters out of options and singletons. Extend
`CodecContractTests`, the corpus and Oracle tests (malformed and truncated input, cancellation, limits, stream
ownership), the AOT smoke consumer and the benchmark matrix. Do not turn I/O failures into corrupt-image exceptions.

**A processing operation.** Keep numeric work in Algorithms and orchestration in Application. Operations producing
buffers return a `FrameReplacement` per frame to `ProcessingState.ReplaceFrames`, which commits the batch; in-place work
uses writable frames for copy-on-write. Keep row/frame cancellation checks. Test pixel vectors, multi-frame canvases,
cancellation after the first frame and `Clone` independence; add a benchmark and keep scalar/SIMD equality.

**The model or public API.** Put validation on the model operation, keep `Size`, `Rectangle` and `TimeSpan` at public
boundaries, prefer `field` for validated properties, and use extension blocks for facade operations while invariants
stay instance methods. Update XML docs, the API snapshot, the guides and `ConsumerCompilationTests`. Before finishing,
run Release, Oracle and scalar-fallback tests and `bash scripts/verify-package-consumer.sh` (trimming and linux-x64
Native AOT). CI covers x64; ARM64 needs a separate hardware run.
