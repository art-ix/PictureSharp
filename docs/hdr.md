# HDR photos with gain maps

iPhone photos exported as JPEG (and Android Ultra HDR 1.1 photos) store an ordinary SDR JPEG, which every viewer shows,
followed by a smaller **gain map** JPEG telling HDR displays how much brighter each area may be. PictureSharp reads the
map, reconstructs the HDR image in linear light, keeps the map through geometric edits and writes it back to JPEG.

| Model | Where it comes from | Metadata |
| --- | --- | --- |
| ISO 21496-1 | iOS 18 and later (Adaptive HDR), Android Ultra HDR 1.1 | Binary ISO metadata in APP2 of the map |
| Apple HDR Gain Map | iOS 14–17 | Headroom in the Apple MakerNote, `HDRGainMapVersion` in the map's XMP |

Recent iPhones write both; ISO 21496-1 wins. Maps described only by Ultra HDR XMP (`hdrgm`) or with an HDR base are
detected but not decoded; other containers can supply maps through a [plugin](#gain-maps-from-format-plugins).

## Loading

HDR is opt-in: `DecoderOptions.LoadGainMap` attaches the compressed map without decoding it. The base pixels are the
same as without the option, and a damaged or unsupported map never fails the load; `image.Hdr` describes it. Without the
option nothing about HDR is parsed.

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

`HdrInfo` reports every detected model (`Detected`), the one used (`Selected`), the map size, the maximum headroom as a
linear ratio (4 means two stops above SDR white) and whether the headers allow `ToHdr` (`CanDecode`).

## Reconstruction

`image.ToHdr(options)` returns an `HdrImage` of read-only `Rgba128` pixels (four `float` channels, straight alpha) in
linear light, where 1 is SDR white; highlights exceed 1 and gamut conversion may give negative values. It exposes
`ColorSpace` (`LinearDisplayP3` for iPhone photos or `LinearSrgb`), `Headroom`, `GainMapKind`, the metadata and pixel
access (`GetRowSpan`, `GetPixelSpan`, `GetPixelMemory`, `CopyPixelDataTo`). `Rgba128` converts to and from `Vector4`.

| `HdrOptions` | Default | Meaning |
| --- | --- | --- |
| `TargetHeadroom` | null (full) | Largest headroom to render, linear (≥ 1). 2 renders up to one stop above SDR white; 1 gives the linearized base. |
| `ColorSpace` | null | Output primaries; null keeps the base image's (Display P3 or sRGB). |
| `MaxDegreeOfParallelism` | -1 | Threads; the result does not depend on it. |

Powers and exponentials use PictureSharp's own `float` functions (fixed-order polynomials, within 2 ulp) instead of the
C runtime, so `ToHdr` pixels and `ToneMap` bytes are identical on x64 and ARM64, with or without SIMD and for any thread
count. With SIMD, a 12 MP photo takes about 35–55 ms for `ToHdr` and 50 ms for `ToneMap` on an i5-12500H; see
[Performance](performance.md#simd-for-hdr-and-graphics-067).

`ToHdr` throws `InvalidOperationException` without a map, `NotSupportedException` for unsupported variants and
`ImageFormatException` for damaged maps. `HdrImage` owns its 16-byte-per-pixel buffer.

### What keeps or removes the map

Geometric operations edit the map together with the base; everything else removes it (`image.Hdr` becomes null):

| Operation | Gain map |
| --- | --- |
| `Resize` (`Stretch`, `Max`, `Min`, `Crop`), `Thumbnail` | kept, resampled with the same filter |
| `Crop` | kept: sliced when the rectangle lies on the map grid, otherwise resampled bilinearly |
| `AutoOrient`, `Rotate(RotateMode)`, `Rotate` by multiples of 90°, `Flip`, `RotateFlip` | kept, same array operation |
| `Resize` with padding (`Pad`), `Pad`, rotation by other angles, color, tone, filters, drawing, masks, `Apply` | removed |
| Frame insertion, removal or reordering | removed |
| `Clone()`, `Clone(operation)` | kept, then the rules above apply to the copy |

The edited map keeps the map-to-base ratio and is resampled deterministically in its 8-bit codes.

- `ApplyExifOrientation` and `TargetSize` while loading are the cheapest route: no map decode until `ToHdr`, and with
  `TargetSize` the map uses a reduced inverse DCT.
- `ColorConversion = ToSrgb` clips colors outside sRGB while loading; keep `None` for the full Display P3 gamut.
- Map pixels count toward `MaxPixels`; a map over the limit makes `HasHdr` false.

## Saving with the gain map

```csharp
using Image photo = Image.Load("IMG_8113.jpeg", new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true });
photo.Mutate(x => x.Resize(1600, 0));
photo.SaveAsJpeg("small-hdr.jpeg", new JpegEncoderOptions { Quality = 90, GainMap = JpegGainMapMode.Preserve });
```

| Mode | Map attached and ISO 21496-1 | No map, Apple-only map or unusable map |
| --- | --- | --- |
| `None` (default) | SDR file, byte for byte as without the map | SDR file |
| `Preserve` | file with the map | SDR file |
| `Require` | file with the map | `InvalidOperationException` (no map), `NotSupportedException` (Apple-only or unsupported), `ImageFormatException` (damaged) |

The file follows libultrahdr v2.0.2 defaults: ISO 21496-1 metadata only (copied from the source byte for byte) and an
MPF directory. No Ultra HDR XMP is generated, and a user XMP packet declaring a gain map is left out. An unedited map is
copied without re-encoding; an edited one is encoded with `GainMapQuality` (default 95), gray maps as grayscale and RGB
maps without chroma subsampling. Apple-only maps cannot be converted to ISO 21496-1.

A map attached by a format plugin (for example from HEIC) is always encoded like an edited map, with ISO 21496-1
metadata written from its values (`GainMapMetadata.ToIso()`, the fractions libultrahdr chooses). When the gain is
applied in primaries other than the base image's, the map gets the ICC profile libultrahdr writes for them (sRGB or
Display P3); otherwise the metadata uses the base color space.

## Tone mapping

`hdr.ToneMap(options)` returns an 8-bit sRGB `Image` with a deterministic operator (it does not imitate the Photos app):

1. Convert to linear sRGB.
2. Multiply by 2^`Exposure` (default 0).
3. Extended Reinhard on Rec. 709 luminance with white point W (`WhitePoint`, default `hdr.Headroom`):
   `Lout = L × (1 + L / W²) / (1 + L)`, with the same scale for R, G and B. `WhitePoint = 1` is plain clipping.
4. Clip each channel to [0, 1], apply the sRGB transfer function and round to 8 bits.

The result's metadata has no ICC profile. Whether to use the SDR base (what the camera chose for SDR displays) or a
tone-mapped rendering is up to the application.

## Editing metadata of HDR files

`Image.EditMetadata` keeps both compressed images byte for byte and updates the MPF offsets. EXIF (for example
`ExifProfile.RemoveGps()`) and XMP can be edited or removed. Changing the ICC profile or removing data the selected
model needs (the Apple MakerNote) throws `InvalidOperationException`; other MPO files throw `NotSupportedException`.

```csharp
Image.EditMetadata("IMG_8113.jpeg", "share.jpeg", m => m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));
```

## Command line

```bash
dotnet run -c Release --project samples/PictureSharp.Cli -- info IMG_8113.jpeg
dotnet run -c Release --project samples/PictureSharp.Cli -- convert IMG_8113.jpeg tone-mapped.webp -hdr tone-map -exposure -0.3
dotnet run -c Release --project samples/PictureSharp.Cli -- resize IMG_8113.jpeg small-hdr.jpg 1600 0 -hdr keep
```

`convert` keeps the SDR base by default; `-hdr tone-map` renders with `-exposure` and `-white`, and `-hdr keep` writes a
JPEG with the ISO 21496-1 map (not for Apple-only maps, padding or other formats).

## Gain maps from format plugins

A plugin can attach its container's map (for example HEIC) with `ImageDecodeContext.AttachGainMap`; loading, `ToHdr`,
`ToneMap`, geometric edits and saving as JPEG with the map then work as for JPEG (saving since 0.8.0). A plugin's
encoder writes the attached map with `image.GetGainMap()`. See [Extending PictureSharp](extending-picturesharp.md#hdr-gain-maps).

## Limitations

No gain map generation from HDR pixels (`HdrImage` cannot be saved), no HDR processing beyond the geometric edits, and
no built-in HEIC or AVIF. PNG and WebP output is SDR. Displaying HDR needs a platform API that accepts linear or PQ/HLG
pixels.
