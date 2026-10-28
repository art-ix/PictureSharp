# Extending PictureSharp

There are three ways to extend PictureSharp from an application:

- **Format plugins** add a file format: detection, decoder, encoder and metadata codec.
- **Frame processors** add an operation to `Mutate` and `Clone`.
- **C# 14 extension blocks** give both a short, built-in-looking API (`image.SaveAsRawa()`, `x.LeftHalf()`).

Plugins use only public API and work under trimming and Native AOT. The last section is for library contributors.

## Format plugins

### A complete plugin

The example below implements a made-up format, RAWA: the bytes `RAWA`, the width and height as 32-bit little-endian
integers, then the RGBA pixels row by row. It is short, but it is a working plugin.

```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PictureSharp;
using PictureSharp.Formats;

public sealed class RawaFormat : IImageFormatModule
{
    // Create the format once: formats compare by identity.
    public static ImageFormat Format { get; } = ImageFormat.Create("RAWA", "image/x-rawa", ".rawa");

    ImageFormat IImageFormatModule.Format => Format;
    public int HeaderSize => 4;                                      // bytes IsSupported needs, 1–4096
    public bool IsSupported(ReadOnlySpan<byte> header) => header.StartsWith("RAWA"u8);
    public IImageFormatDecoder? Decoder { get; } = new RawaDecoder();
    public IImageFormatEncoder? Encoder { get; } = new RawaEncoder(); // omit for a read-only format
}

public sealed record RawaEncoderOptions : EncoderOptions
{
    public override ImageFormat Format => RawaFormat.Format;
    public bool Opaque { get; init; }                                // write A = 255
}

sealed class RawaDecoder : IImageFormatDecoder
{
    public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
    {
        (int width, int height) = ReadSize(data.Span);
        return new()
        {
            Format = RawaFormat.Format,
            Width = width,
            Height = height,
            Color = new ImageColorInfo                               // required: what the header states
            {
                Model = ImageColorModel.Rgb,
                ChannelCount = 4,
                BitsPerSample = 8,
                SampleFormat = ImageSampleFormat.UnsignedInteger,
                AlphaMode = ImageAlphaMode.Straight,
                HasIccProfile = false,
            },
            Compression = ImageCompression.Lossless,
        };
    }

    public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
    {
        (int width, int height) = ReadSize(data.Span);
        context.ReservePixels(width, height, PixelType.Rgba32);       // fails on MaxPixels before allocating
        ReadOnlySpan<Rgba32> pixels = MemoryMarshal.Cast<byte, Rgba32>(data.Span[12..]);
        var image = new Image(width, height);
        for (int y = 0; y < height; y++)
        {
            context.ThrowIfCancellationRequested();
            pixels.Slice(y * width, width).CopyTo(image.GetRowSpan<Rgba32>(y)); // truncated file: reported as corrupt
        }

        return image;
    }

    static (int Width, int Height) ReadSize(ReadOnlySpan<byte> data)
    {
        int width = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        return width > 0 && height > 0 ? (width, height) : throw new ImageFormatException(RawaFormat.Format, "Invalid size.");
    }
}

sealed class RawaEncoder : IImageFormatEncoder<RawaEncoderOptions>
{
    // The image is borrowed and the stream stays open.
    public void Encode(Image image, Stream output, RawaEncoderOptions options, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[12];
        "RAWA"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Height);
        output.Write(header);

        var row = new Rgba32[image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            image.GetRowSpan<Rgba32>(y).CopyTo(row);
            if (options.Opaque)
            {
                foreach (ref Rgba32 pixel in row.AsSpan()) pixel.A = 255;
            }

            output.Write(MemoryMarshal.AsBytes(row.AsSpan()));
        }
    }
}
```

### Registering the plugin

Register it once at startup; after that it works like a built-in format:

```csharp
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddFormat(new RawaFormat()).Build();

using var image = Image.Load("picture.rawa");                       // recognized by its signature
image.Save("copy.rawa");                                            // encoder chosen by the extension
image.Save("opaque.rawa", new RawaEncoderOptions { Opaque = true });
```

To use it only in some calls, build a separate configuration and pass it in the options:

```csharp
ImageConfiguration withRawa = ImageConfiguration.Default.ToBuilder().AddFormat(new RawaFormat()).Build();

using var image = Image.Load(bytes, new DecoderOptions { Configuration = withRawa });
image.Save("copy.rawa", new RawaEncoderOptions { Configuration = withRawa });
```

Saves without options (`Save(path)`, `Save(stream, format)`) always use `ImageConfiguration.Default`.

`AddFormat` appends after the registered formats, so built-in signatures are checked first; a duplicate format, name,
MIME type or extension throws `ArgumentException`. `ReplaceFormat` swaps the codecs of a registered format (for example
`ImageFormat.Png`) and keeps its place in detection.

### Shortcuts with extension blocks

A plugin can give its users the same API the built-in formats have:

```csharp
namespace MyCompany.PictureSharp.Rawa;

public static class RawaExtensions
{
    extension(ImageFormat)
    {
        public static ImageFormat Rawa => RawaFormat.Format;          // ImageFormat.Rawa
    }

    extension(ImageConfigurationBuilder builder)
    {
        public ImageConfigurationBuilder AddRawa() => builder.AddFormat(new RawaFormat());
    }

    extension(Image image)
    {
        public void SaveAsRawa(string path, RawaEncoderOptions? options = null) => image.Save(path, options ?? new());
        public byte[] ToRawa(RawaEncoderOptions? options = null) => image.Encode(options ?? new());
    }
}
```

```csharp
ImageConfiguration.Default = ImageConfiguration.Default.ToBuilder().AddRawa().Build();

image.SaveAsRawa("copy.rawa");
byte[] bytes = image.ToRawa(new() { Opaque = true });
image.Save(stream, ImageFormat.Rawa);
```

The shortcuts save through `options.Configuration`, which defaults to `ImageConfiguration.Default`.

### Rules for codecs

**Module and threads**

- The module's members are read once, at registration.
- Codecs must be thread-safe, must not keep the input or context after returning, and their output must not depend on
  `MaxDegreeOfParallelism`.

**Detection**

- `IsSupported` may receive fewer than `HeaderSize` bytes. Index, argument, overflow and invalid-operation errors
  thrown there mean "not this format".

**Decoding**

- Call `context.ReservePixels` with the pixel type of the image you will return before allocating pixels, and
  `context.ReserveMetadata` before copying decompressed ICC, EXIF or XMP data. A source with more than 8 bits per
  sample decodes to `PixelType.Rgba64` when `context.Options.PreserveBitDepth` is set, otherwise to `Rgba32`; scale
  n-bit samples with `(v * 65535 + max / 2) / max`, `max = (1 << n) - 1`, so white maps to 65535.
- Throw `ImageFormatException` for corrupt data and `NotSupportedException` for valid data using an unsupported
  feature. Index, argument, overflow, invalid-operation and cast errors are reported as `ImageFormatException`, so
  `data.Span[offset..]` on a truncated file needs no extra check.
- The library rechecks `MaxPixels` and `MaxFrames`, sets `SourceFormat` and applies `TargetSize` and
  `ApplyExifOrientation`; the decoder returns the image as stored.

**Identifying**

- `Identify` reads headers only. `Color` and `Compression` are required: report what the file states or the format
  defines, never a guess from pixels, and use `ImageColorModel.Other`, `ImageSampleFormat.Other` or
  `ImageCompression.Unknown` for what the header cannot tell.
- Fill `Color` regardless of `IgnoreMetadata`, and check for an ICC profile without copying it, so `Identify` reserves
  no metadata for it. `Cicp` takes HEIF nclx or an equivalent enumerated description; leave it null when H.273 cannot
  express it exactly.
- Report `FrameCount = null` only when the count needs decoding, and `Resolution = null` when the file declares none.

**Encoding**

- Encoder options derive from `EncoderOptions` and override `Format`. `IImageFormatEncoder<TOptions>` creates the
  defaults with `new TOptions()` and rejects options of another type with `ArgumentException`.
- An encoder receives `Rgba32` images unless it overrides `SupportsPixelType` to accept `PixelType.Rgba64`; the
  library then passes 16-bit images as they are, otherwise it passes an 8-bit copy rounded to the nearest value.

### Orientation and color

- **Orientation stored outside EXIF** (HEIF `irot`/`imir`): apply it to the pixels and set `Metadata.Orientation` to
  what remains, usually `TopLeft`. `Identify` reports the displayed size.
- **Color**: put an ICC profile in `Metadata.IccProfile`, or map nclx/CICP primaries to `Metadata.Chromaticities` and a
  pure power transfer to `Metadata.Gamma` (`Chromaticities` alone mean the sRGB curve). PQ and HLG cannot be expressed:
  throw `NotSupportedException` or leave them undeclared.

### Containers, strips and tiles

A container format decodes a whole embedded image with `context.DecodeEmbedded(data)`. It uses the same codecs, limits
and options as the load.

A format that stores one image as strips or tiles in another format (JPEG tiles in TIFF) reserves the image once and
decodes each part with `context.DecodeEmbeddedPart`:

```csharp
context.ReservePixels(width, height, PixelType.Rgba32);
var image = new Image(width, height);
foreach ((ReadOnlyMemory<byte> data, int x, int y) in tiles)
{
    using Image tile = context.DecodeEmbeddedPart(data, tileWidth, tileHeight);
    // Copy the part of the tile that lies inside the image; check that the tile covers it.
}
```

- A part is not charged against the reserved image and is not a frame. It may decode to at most `width` x `height`
  pixels, which must itself fit `MaxPixels`.
- It is decoded as stored: without EXIF orientation, gain map, color conversion or metadata.
- Dispose each part before decoding the next, so a load holds at most the image and one part.

Embedded images are found by their signature, so a corrupt file may point at itself or at another copy of its
container. Images nest at most 8 levels below the loaded file (each `DecodeEmbedded` or `DecodeEmbeddedPart` is one
level); a deeper one throws `ImageFormatException` naming the format that asked for it, so a plugin need not guard
against recursion itself.

`EmbeddedPartOptions` replaces the load's settings for one part; a null setting keeps the load's own:

```csharp
var cmyk = new EmbeddedPartOptions { Jpeg = new JpegDecoderOptions { CmykStorage = JpegCmykStorage.Plain } };
using Image strip = context.DecodeEmbeddedPart(data, stripWidth, stripHeight, cmyk);

// Several parts decoded at once by the container:
var serial = new EmbeddedPartOptions { MaxDegreeOfParallelism = 1 };
```

### Decoder options

A plugin that lets the caller choose what to decode (a TIFF page, a DDS mip level, a HEIF item) defines a record
derived from `FormatDecoderOptions` (0.7.6). Callers pass it in `DecoderOptions.FormatOptions`:

```csharp
public sealed record TiffDecoderOptions : FormatDecoderOptions
{
    public int Page { get; init => field = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(Page)); }
}

using var image = Image.Load(path, new DecoderOptions { FormatOptions = [new TiffDecoderOptions { Page = 2 }] });
```

The plugin reads its record with `GetFormatOptions<T>()`, and uses the same selection in `Identify`, `Decode` and the
metadata codec:

```csharp
public ImageInfo Identify(ReadOnlyMemory<byte> data, DecoderOptions options)
{
    int page = options.GetFormatOptions<TiffDecoderOptions>()?.Page ?? 0;
    // ...
}

public Image Decode(ReadOnlyMemory<byte> data, ImageDecodeContext context)
{
    int page = context.Options.GetFormatOptions<TiffDecoderOptions>()?.Page ?? 0;
    // ...
}
```

- Without its record, a plugin decodes what it did before. Plugins ignore records of other plugins and built-in formats
  ignore them all, so one `DecoderOptions` can carry settings for several plugins.
- `FormatOptions` holds at most one record per type and is copied when set; null entries and duplicate types throw
  `ArgumentException`.
- Validate values in the record (`ArgumentOutOfRangeException`). Throw `ImageFormatException` when the selected item is
  not in the file, as `Ico.Index` does. `ContainerItemCount` still counts every item.
- Limits, cancellation and `ReservePixels` apply to the selected item. `DecodeEmbedded` passes the same options on;
  `DecodeEmbeddedPart` passes the limits and format options (with `EmbeddedPartOptions` replacing `Jpeg` and
  `MaxDegreeOfParallelism`).
- Frame readers (`OpenFrameReaderAsync`) do not support plugin formats.

### Metadata without pixels

Set `IImageFormatModule.MetadataCodec` to support `Image.ReadMetadata` and `Image.EditMetadata` for the format:

- `Read` returns the ICC, EXIF and XMP blocks, charged with `context.ReserveMetadata`.
- `Rewrite` returns the file with the edited blocks. Its default implementation throws `NotSupportedException`, so a
  read-only codec implements only `Read`.

The library checks limits and cancellation, skips edits that change nothing and writes files atomically.

### Format metadata

Data that only your format understands (a hotspot, an author field, extension blocks) goes in an immutable record
derived from `FormatMetadata`. Set it in the decoder unless `IgnoreMetadata` is set, and read it in the encoder, with
an explicit encoder option taking precedence:

```csharp
public sealed record XbmMetadata(Point? Hotspot) : FormatMetadata;

// Decode:
if (!context.Options.IgnoreMetadata && hotspot is { } point)
    image.Metadata.SetFormatMetadata(new XbmMetadata(point));

// Encode:
Point? hotspot = options.Hotspot ?? image.Metadata.GetFormatMetadata<XbmMetadata>()?.Hotspot;
```

- One entry per record type; setting another replaces it. Charge the raw bytes you read with
  `context.ReserveMetadata`; the records themselves are not counted.
- Operations that move pixels (resize, crop, rotate, flip, pad, `TargetSize`) remove every record, because the library
  cannot adjust them. Do not rely on a record surviving an edit.
- A `MetadataCodec` may return records from `Read` and write them in `Rewrite`, like the ICC, EXIF and XMP blocks.
- Whether `EncoderOptions.Metadata` (ICC, EXIF, XMP flags) also controls your record is your format's decision; document it.

### HDR gain maps

#### Reading

When `context.Options.LoadGainMap` is set, a decoder that finds a gain map attaches it; `Image.GainMap`, `ToHdr` and
`ToneMap` then work as for JPEG. The map is decoded lazily, by a `GainMapImage`:

```csharp
sealed class ContainerGainMap(byte[] mapData, Size size) : GainMapImage(size)
{
    // Called by ToHdr or the first geometric edit, possibly on another thread after the load returned.
    public override Image Decode(int maxDegreeOfParallelism, CancellationToken cancellationToken) =>
        Image.Load(mapData, new DecoderOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });
}

// In Decode, after decoding the base image:
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

- `GainMapMetadata.ParseIso` reads an ISO 21496-1 payload, `ParseApple` the Apple model.
- A `GainMapImage` owns its data, never uses the decode context and returns the map at its stored size.
- In `Identify`, report `ImageInfo.GainMap = metadata.ToGainMapInfo(mapSize)` when `LoadGainMap` is set.
- When a file describes the map in several models (iPhones since iOS 18 store ISO 21496-1 and Apple metadata), pass
  them all, `metadata.Kind` included: `AttachGainMap(metadata, map, detected)` in `Decode` and
  `metadata.ToGainMapInfo(mapSize) with { Detected = detected }` in `Identify`. `Image.GainMap.Detected` then matches a JPEG
  (0.7.1).
- Keep the base image's ICC profile or `Chromaticities`/`Gamma` when attaching a map, also with `IgnoreMetadata`.
- Map pixels count toward `MaxPixels`. An oversized map, unparsable metadata or a base color other than sRGB or Display
  P3 is reported through `Image.GainMap` and never fails the load.
- The library carries the map through `TargetSize`, orientation, color conversion and geometric edits. Saving as JPEG
  writes it like an edited JPEG map, with metadata from `ToIso()` (0.8.0).

#### Writing (0.8.0)

An encoder gets the attached map with `image.GetGainMap()` (namespace `PictureSharp.Formats`). It decodes nothing;
`Map.Decode` returns a new image on every call:

```csharp
// In Encode:
if (image.GetGainMap() is { Metadata: var metadata, Map: var map })
{
    byte[] iso = metadata.ToIso();             // the ISO 21496-1 payload, as ParseIso reads it
    byte[]? icc = metadata.ToAlternateIcc();   // null when the base primaries apply
    if (map is ContainerGainMap own)
    {
        // Unedited and unrotated since this plugin attached it: copy the compressed map as is.
    }
    else
    {
        using Image pixels = map.Decode(maxDegreeOfParallelism: -1, cancellationToken);
        // Encode it: it has the size and orientation of the image; a gray map has R = G = B.
    }
}
```

- `GetGainMap()` returns null when no map can be written; `GetGainMap(require: true)` throws instead, like
  `JpegGainMapMode.Require`.
- The metadata is always ISO 21496-1: a map with Apple metadata only is converted, with recomputed map codes
  (0.8.1).
- `ToIso()` stores the multiplication color space only as a flag. When `metadata.MultiplicationSpace` is not null,
  store `ToAlternateIcc()` with the map: as the map's ICC profile in JPEG, in the `colr` box of a HEIF `tmap` item.

#### Floating-point HDR sources (0.8.1)

A plugin that reads linear HDR pixels (for example an EXR or float TIFF decoder) can hand them to the library as an
`HdrImage` and return the SDR rendering with a generated ISO 21496-1 gain map, which then saves as Ultra HDR JPEG or
through any encoder that writes `GetGainMap()`:

```csharp
// linear: Rgba128 pixels in linear light, 1 = SDR white, row by row.
using var hdr = new HdrImage(width, height, linear, HdrColorSpace.LinearSrgb);
Image image = hdr.ToGainMapImage(new GainMapOptions { ChannelCount = 1 });   // base: hdr.ToneMap()
```

### SPI compatibility

The plugin SPI and the model plugins fill only grow: interfaces get new members only with a default implementation,
and new parameters come as overloads. 0.8.5 broke it once for the new `ImageInfo`; plugins built against an older
version are rejected at registration. [Stable plugin SPI](plugin-spi.md) lists the
types and the rules, and how to choose the minimum PictureSharp version of a plugin.

## Custom frame processors

`IFrameProcessor` (`PictureSharp.Processing`) produces a new pixel buffer for every frame and may change the canvas size.
This one keeps the left half of the image:

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

- `GetOutputSize` is called once per operation, since all frames share one canvas; both sides must be positive.
- `Process` receives row-major straight-alpha pixels and a transparent black destination. Do not keep the spans.
- All frames are prepared before any is published, so an exception or cancellation leaves the image unchanged. Within
  `Clone` the source is not copied.
- A struct processor runs without boxing.

For edits that keep the size, `Apply(Action<ImageFrame>)` is simpler; see
[Image processing](image-processing.md#custom-operations).

## Contributing to the library

**A built-in format**

- Put its descriptor in Domain, public options in Application under the format namespace, and detection,
  identification, decoding (`DecodedImage`) and encoding in an Infrastructure module.
- Register it only as a `FormatRegistration` in `Composition/CodecRegistry.cs`; `CodecResolver` builds codecs per
  `ImageConfiguration`. Built-in codecs use the internal contracts; the plugin SPI is adapted to them in
  `Application/Extensibility`.
- Validate stored dimensions and frame counts before allocating. Charge `DecodeContext.Budget` at the leaf decoder
  (embedded formats get a child context) and metadata after decompression. Keep execution counters out of options and
  singletons. Do not turn I/O failures into corrupt-image exceptions.
- Extend `CodecContractTests`, the corpus and Oracle tests (malformed and truncated input, cancellation, limits, stream
  ownership), the AOT smoke consumer and the benchmark matrix.

**A processing operation**

- Keep numeric work in Algorithms and orchestration in Application.
- Operations producing buffers return a `FrameReplacement` per frame to `ProcessingState.ReplaceFrames`, which commits
  the batch; in-place work uses writable frames for copy-on-write. Keep row and frame cancellation checks.
- Test pixel vectors, multi-frame canvases, cancellation after the first frame and `Clone` independence; add a
  benchmark and keep scalar and SIMD results equal.

**The model or public API**

- Put validation on the model operation and keep `Size`, `Rectangle` and `TimeSpan` at public boundaries.
- Prefer `field` for validated properties. Use extension blocks for facade operations; invariants stay instance
  methods.
- Update the XML docs, the API snapshot, the guides and `ConsumerCompilationTests`.

**Before finishing**, run the Release, Oracle and scalar-fallback tests and `bash scripts/verify-package-consumer.sh`
(trimming and linux-x64 Native AOT). CI covers x64; ARM64 needs a separate hardware run.
