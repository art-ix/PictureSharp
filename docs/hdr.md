# HDR photos with gain maps

iPhone photos exported as JPEG (and Android Ultra HDR 1.1 photos) are HDR images stored in two parts: an ordinary SDR
JPEG, which every viewer shows, and a smaller **gain map** JPEG appended after it, which tells HDR displays how much
brighter each area may be. PictureSharp 0.6.5 reads the gain map and reconstructs the HDR image in linear light.

Two metadata models are supported:

| Model | Where it comes from | Metadata |
| --- | --- | --- |
| ISO 21496-1 | iOS 18 and later (Adaptive HDR), Android Ultra HDR 1.1 | Binary ISO metadata in APP2 of the map |
| Apple HDR Gain Map | iOS 14–17 | Headroom in the Apple MakerNote, `HDRGainMapVersion` in the map's XMP |

Recent iPhones write both; ISO 21496-1 wins. Gain maps described only by Ultra HDR XMP (`hdrgm`, without ISO metadata),
HEIC files and gain maps whose base image is the HDR rendition are detected but not decoded.

## Loading

HDR is opt-in: `DecoderOptions.LoadGainMap` makes `Image.Load` attach the compressed map to the image without decoding
it. The base pixels are exactly the same as without the option, and a damaged or unsupported gain map never fails the
load — it is described by `image.Hdr`.

```csharp
using PictureSharp;
using PictureSharp.Processing;

var options = new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true };
using Image image = Image.Load("IMG_8113.jpeg", options);
if (image.HasHdr)
{
    using HdrImage hdr = image.ToHdr();                    // linear RGB float, SDR white = 1
    Console.WriteLine($"{hdr.Width}x{hdr.Height}, {hdr.ColorSpace}, headroom {hdr.Headroom}");
    ReadOnlySpan<Rgba128> row = hdr.GetRowSpan(0);

    using Image sdr = hdr.ToneMap(new ToneMappingOptions { Exposure = -0.5f });
    sdr.Save("tone-mapped.jpg");
}

image.Save("base.jpg");                                     // the SDR base image, without HDR
```

`Image.Identify` with the same option fills `ImageInfo.Hdr` from the headers only:

```csharp
HdrInfo? hdr = Image.Identify("IMG_8113.jpeg", new DecoderOptions { LoadGainMap = true }).Hdr;
if (hdr is { CanDecode: true })
{
    Console.WriteLine($"{hdr.Selected}, map {hdr.GainMapSize}, up to {hdr.Headroom}x SDR white");
}
else if (hdr is not null)
{
    Console.WriteLine($"Gain map not usable: {hdr.UnsupportedReason}");
}
```

`HdrInfo` reports every detected model (`Detected`), the one used (`Selected`), the stored map size, the maximum
headroom as a linear ratio (4 means two stops above SDR white), and whether `ToHdr` can run. `CanDecode` checks the
headers; the compressed map data is decoded only by `ToHdr`.

Without `LoadGainMap` nothing about HDR is parsed and loading costs exactly what it did before 0.6.5.

## Reconstruction

`image.ToHdr(options)` returns an `HdrImage`: `Rgba128` pixels (four `float` channels, straight alpha) in linear light,
where 1 is SDR white. Highlights are above 1, and gamut conversion may produce negative components; nothing is
clipped. `HdrImage` exposes `Width`, `Height`, `ColorSpace` (`LinearDisplayP3` for iPhone photos or `LinearSrgb`),
`Headroom`, `GainMapKind`, a copy of the metadata, an indexer, `GetRowSpan`, `GetPixelSpan`, `GetPixelMemory` and
`CopyPixelDataTo`. The pixels are read-only; copy them to process further. `Rgba128` converts to and from
`System.Numerics.Vector4`.

| `HdrOptions` | Default | Meaning |
| --- | --- | --- |
| `TargetHeadroom` | null (full) | Largest headroom to render, linear (≥ 1). 2 renders up to one stop above SDR white; 1 gives the linearized base. |
| `ColorSpace` | null | Output primaries; null keeps the base image's (Display P3 or sRGB). |
| `MaxDegreeOfParallelism` | -1 | Threads; the result does not depend on it. |

`ToHdr` throws `InvalidOperationException` when no map is attached, `NotSupportedException` for valid but unsupported
variants and `ImageFormatException` for damaged maps. `HdrImage` owns its buffer (16 bytes per pixel, four times an
`Image`) and stays valid after the source image is disposed.

### What keeps or removes the map

The map describes the loaded base pixels, so:

- every `Mutate`, frame insertion, removal or reordering removes it (`image.Hdr` becomes null), including `AutoOrient`
  and `Resize`; use `ApplyExifOrientation` and `TargetSize` while loading to get an oriented or smaller HDR image;
- `Clone()` keeps it, `Clone(operation)` does not;
- writes through pixel views are not detected: `ToHdr` uses the current pixels;
- `ColorConversion = ToSrgb` still allows `ToHdr`, but colors outside sRGB were clipped while loading; load with the
  default `ColorConversion.None` to keep the full Display P3 gamut;
- encoders never write the map or HDR declarations: `Save` produces an SDR file.

With `TargetSize`, `ToHdr` reconstructs at the reduced size and decodes the map with a reduced inverse DCT, so no
full-size buffer is needed for thumbnails. The map's pixels count towards `MaxPixels` when it is attached; a map over
the limit makes `HasHdr` false instead of failing the load.

## Tone mapping

`hdr.ToneMap(options)` returns a new 8-bit sRGB `Image`. The operator is deterministic and documented; it does not try
to imitate the Photos app:

1. Convert to linear sRGB.
2. Multiply by 2^`Exposure` (default 0).
3. Extended Reinhard on Rec. 709 luminance with white point W (`WhitePoint`, default `hdr.Headroom`):
   `Lout = L × (1 + L / W²) / (1 + L)`, with the same scale for R, G and B. `WhitePoint = 1` is plain clipping.
4. Clip each channel to [0, 1], apply the sRGB transfer function and round to 8 bits.

The result's metadata is a copy without the ICC profile (the pixels are sRGB). Choosing between the SDR base image
(`image.Save`) and a tone-mapped HDR rendering (`ToHdr` + `ToneMap`) is up to the application: the base image is what
the camera chose for SDR displays.

## Editing metadata of HDR files

`Image.EditMetadata` rewrites JPEG files with a valid gain map without recompression: both compressed images are kept
byte for byte and the MPF offsets are updated. EXIF (for example `ExifProfile.RemoveGps()`) and XMP can be edited or
removed. Changing or removing the ICC profile, or removing data the selected model needs (the Apple MakerNote when the
Apple model is used), throws `InvalidOperationException` and leaves the destination untouched. Other multi-picture
files (MPO) are still rejected with `NotSupportedException`.

```csharp
Image.EditMetadata("IMG_8113.jpeg", "share.jpeg", m => m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));
```

## Command line

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- info IMG_8113.jpeg
dotnet run -c Release --project samples/PictureSharp.Cli -- convert IMG_8113.jpeg tone-mapped.webp -hdr tone-map -exposure -0.3
```

`info` prints the gain map model, size and headroom. `convert` keeps the SDR base image by default (`-hdr base`);
`-hdr tone-map` renders the HDR reconstruction with `-exposure` and `-white`.

## Limits of this release

No HDR encoder or gain map generation, no HDR processing in `Mutate`, no HEIC/AVIF. Saving to PNG or WebP produces
SDR. Displaying HDR is up to the application, which needs a platform API that accepts linear or PQ/HLG pixels.
