# Metadata

`image.Metadata` (`ImageMetadata`) carries the resolution and the raw ICC, EXIF and XMP blocks. Decoders fill it, processing keeps it, and encoders write it back.

```csharp
using PictureSharp;

using var photo = Image.Load("photo.jpg");
ImageMetadata meta = photo.Metadata;
Console.WriteLine($"{meta.HorizontalResolution} × {meta.VerticalResolution} DPI, orientation {meta.Orientation}");
Console.WriteLine($"ICC: {meta.GetBlockLength(MetadataKinds.Icc)} bytes, EXIF: {meta.GetBlockLength(MetadataKinds.Exif)} bytes, XMP: {meta.GetBlockLength(MetadataKinds.Xmp)} bytes");

meta.HorizontalResolution = 300;          // must be positive and finite
meta.VerticalResolution = 300;
meta.Xmp = null;                          // drop one block
meta.Clear();                             // drop ICC, EXIF and XMP (the resolution stays)
```

| Member | Meaning |
| --- | --- |
| `HorizontalResolution`, `VerticalResolution` | DPI; 96 when the file does not specify it. |
| `IccProfile` | Raw ICC profile. Carried over, not applied: PictureSharp does no color management. |
| `Exif` | Raw EXIF as a TIFF stream (`II*\0`/`MM\0*`); a JPEG `Exif\0\0` prefix is stripped when set. |
| `Xmp` | Raw XMP packet (UTF-8 XML). |
| `Orientation` | The EXIF orientation tag (`ExifOrientation`), read and written inside the EXIF bytes; `Unknown` without EXIF. |
| `IsEmpty`, `Clear()`, `Clone()` | Whether ICC/EXIF/XMP are absent; remove them; deep copy. |

`Image.Identify` also reports `Orientation` and the resolution without decoding pixels.

## EXIF orientation

Cameras often store photos sideways and record the rotation in EXIF. Make the pixels upright either while loading or later:

```csharp

using var upright = Image.Load("photo.jpg", new DecoderOptions { ApplyExifOrientation = true });

using var photo = Image.Load("photo.jpg");
photo.Mutate(x => x.AutoOrient());        // rotates/flips for orientations 2–8 and resets the tag to TopLeft
```

When a supported orientation tag exists, both reset it to `ExifOrientation.TopLeft`, so viewers do not rotate the image a second time. An absent tag stays absent.

## Choosing what to write

All encoder options have a `Metadata` property (`MetadataKinds` flags: `Icc`, `Exif`, `Xmp`, `All`, `None`):

```csharp
using PictureSharp.Formats.Jpeg;
using PictureSharp.Formats.WebP;

photo.SaveAsWebP("share.webp", new() { Metadata = MetadataKinds.Icc });     // keep colors, drop EXIF (GPS) and XMP
photo.SaveAsJpeg("clean.jpg", new() { Metadata = MetadataKinds.None });
```

| Format | ICC | EXIF | XMP | Resolution |
| --- | --- | --- | --- | --- |
| WebP | ✔ | ✔ | ✔ | — |
| PNG | ✔ (`iCCP`) | ✔ (`eXIf`) | ✔ (`iTXt`) | ✔ (`pHYs`) |
| JPEG | ✔ (APP2, split across segments) | ✔ (APP1) | ✔ (APP1) | ✔ (JFIF) |
| BMP | ✔ (`BITMAPV5HEADER`) | — | — | ✔ |
| GIF | ✔ (`ICCRGBG1012` extension) | — | ✔ (`XMP DataXMP` extension) | — |
| PCX | — | — | — | ✔ (header DPI) |

JPEG EXIF and XMP must each fit in one APP1 segment (about 64 KB); larger blocks throw `ArgumentException`. To skip reading ICC, EXIF and XMP blocks, load with `new DecoderOptions { IgnoreMetadata = true }`.

## Ownership and editing

`IccProfile`, `Exif` and `Xmp` copy on assignment and return independent snapshots. Editing a returned array does not edit the image. Assign the edited block back or use `SetBlock(kind, span)`. Internal codecs transfer freshly decoded buffers without a second copy and read owned blocks without allocating snapshots.

`GetBlockLength(kind)` returns the required buffer size without copying; `CopyBlockTo(kind, destination)` copies into caller-owned memory. Both select exactly one kind. `RemoveBlocks(flags)` removes selected blocks and keeps DPI.

```csharp
var metadata = new ImageMetadata();
metadata.Orientation = ExifOrientation.RightTop; // creates missing EXIF/orientation
byte[] bytes = new byte[metadata.GetBlockLength(MetadataKinds.Exif)];
metadata.CopyBlockTo(MetadataKinds.Exif, bytes);
bool updated = metadata.TrySetOrientation(ExifOrientation.TopLeft);
metadata.RemoveBlocks(MetadataKinds.Xmp | MetadataKinds.Icc);
```

`Orientation` accepts values 1–8 and creates a missing tag. It rejects malformed existing EXIF without changing the block. `TrySetOrientation` updates only a supported existing tag and returns false when missing or malformed. Unknown EXIF payloads are retained when a new directory is appended. This is a minimal orientation editor, not a general EXIF/MakerNotes editor.

## Typed EXIF

Typed profiles and metadata-only editing are available since 0.6.1.

`ExifProfile` is a live view of the same EXIF bytes exposed by `Exif`. It is always available; reading an empty
profile does not create a block. Assigning a raw block, setting `Orientation`, clearing metadata or editing the
profile is immediately visible through the other APIs. A cloned `ImageMetadata` owns an independent profile.

```csharp
using PictureSharp;

ImageMetadata metadata = Image.ReadMetadata("photo.jpg");
if (metadata.ExifProfile.TryGetValue(ExifTag.Model, out var model))
    Console.WriteLine(model);
if (metadata.ExifProfile.TryGetValue(ExifTag.ExposureTime, out var exposure))
    Console.WriteLine($"{exposure.Numerator}/{exposure.Denominator} s");

metadata.ExifProfile.SetValue(ExifTag.DateTimeOriginal, new DateTime(2026, 10, 15, 12, 0, 0));
metadata.ExifProfile.SetValue(ExifTag.Model, "Camera");
metadata.ExifProfile.RemoveValue(ExifTag.CameraOwnerName);
metadata.ExifProfile.KeepOnly(ExifTag.Model, ExifTag.DateTimeOriginal, ExifTag.Orientation);
```

The initial supported tags are:

| Tags | Value type |
| --- | --- |
| `Orientation` | `ExifOrientation` (1–8) |
| `Make`, `Model`, `CameraOwnerName` | ASCII `string` |
| `DateTime`, `DateTimeOriginal`, `DateTimeDigitized` | `DateTime` with `Kind = Unspecified` |
| `XResolution`, `YResolution`, `ExposureTime`, `FNumber`, `FocalLength` | `ExifRational` |
| `ResolutionUnit` | `ExifResolutionUnit`: `None`, `Inch`, `Centimeter` |
| `IsoSpeed` | `ushort`: the legacy 16-bit ISO sensitivity tag |
| `GpsLatitudeRef`, `GpsLongitudeRef`, `GpsDateStamp` | ASCII `string`; hemisphere N/S or E/W |
| `GpsLatitude`, `GpsLongitude`, `GpsTimeStamp` | Three `ExifRational` values: degrees/minutes/seconds or hours/minutes/seconds |
| `GpsAltitudeRef`, `GpsAltitude` | `ExifGpsAltitudeReference` (`AboveSeaLevel`, `BelowSeaLevel`), `ExifRational` meters |

`ExifRational` preserves an exact unsigned numerator and denominator; `ToDouble()` is a convenience conversion.
A zero denominator is rejected on write. EXIF dates in this API do not include offsets or fractional seconds;
these additional tags remain preserved as opaque data. Missing tags and unsupported value shapes return false
from `TryGetValue`. Malformed TIFF structure throws `ImageFormatException`; unsupported TIFF types throw
`NotSupportedException`. Arrays read from a profile are independent copies.

Edits preserve unknown entries, MakerNotes payload bytes and their original absolute offsets. Unsupported
structures are rejected rather than guessed. `KeepOnly` deliberately rebuilds a small profile containing only
the requested supported tags; it discards thumbnails, unknown fields and MakerNotes. A failed edit leaves the
previous raw block intact. As with the other model APIs, concurrent mutation is unsupported.

### GPS position

`TryGetGpsLocation` and `SetGpsLocation` convert between the raw degree/minute/second and hemisphere tags and a
`GpsLocation` in signed decimal degrees, with an optional altitude in meters:

```csharp
using PictureSharp;

var metadata = new ImageMetadata();
metadata.ExifProfile.SetGpsLocation(new GpsLocation(-33.8688, 151.2093, altitude: 58));
if (metadata.ExifProfile.TryGetGpsLocation(out GpsLocation location))
    Console.WriteLine($"{location.Latitude:F4}, {location.Longitude:F4}, {location.Altitude} m");
bool removed = metadata.ExifProfile.RemoveGps(); // true: the GPS directory existed
```

`GpsLocation` accepts latitude in [−90, 90] and longitude in [−180, 180]. Writing stores whole degrees and minutes
and seconds rounded to 1/10000″ (about 3 mm), and altitude to millimeters; a `null` altitude removes the altitude
tags. Other GPS tags, such as the time stamp, are kept. Reading requires both coordinates and both hemisphere
references; a missing altitude reference means above sea level. Incomplete or out-of-range values return false.

## Editing a file without recompression

`Image.ReadMetadata` reads JPEG, PNG/APNG or WebP metadata without decoding pixels. `Image.EditMetadata` changes
those containers without decoding or re-encoding image data. Every existing JPEG scan, PNG IDAT/APNG fdAT payload
and WebP image/animation payload is copied byte for byte. Animation controls and safe unknown segments/chunks are
preserved. There is no fallback to `Load` / `Save`. Compressed PNG metadata may be decompressed and recompressed;
this never involves the pixel payloads.

```csharp
using PictureSharp;

// Remove the standard EXIF GPS directory and its data bytes.
Image.EditMetadata("photo.jpg", "share.jpg", m => m.ExifProfile.RemoveGps());

// Keep only color information among the supported raw blocks.
Image.EditMetadata("photo.jpg", "colors.jpg", m =>
    m.RemoveBlocks(MetadataKinds.Exif | MetadataKinds.Xmp));

// Replace a raw profile or edit typed values using the same ImageMetadata model.
Image.EditMetadata("photo.jpg", "edited.jpg", m =>
{
    m.ExifProfile.SetValue(ExifTag.Model, "Camera");
    m.ExifProfile.SetValue(ExifTag.ExposureTime, new ExifRational(1, 125));
});

// The same path is supported: replace the original only after successful completion.
await Image.EditMetadataAsync("photo.jpg", "photo.jpg", m => m.Clear());
```

The overloads follow `Load`: synchronous methods take `(…, DecoderOptions? options = null)` or
`(…, DecoderOptions? options, CancellationToken)`, asynchronous methods `(…, CancellationToken = default)` or
`(…, DecoderOptions? options, CancellationToken = default)`. Memory overloads return a new array and never modify
the input; byte arrays bind to the `ReadOnlyMemory<byte>` overload without copying.

Changing orientation or ICC metadata can change how a viewer displays the unchanged encoded pixels.
`RemoveGps` applies to standard EXIF GPS directories; it does not inspect private MakerNotes or XMP.
`Clear()` removes ICC/EXIF/XMP, preserving resolution and unrelated container information such as JPEG IPTC or
PNG text. Neither operation promises complete anonymization of arbitrary embedded information.

Path output is prepared in a separate temporary file in the destination directory. The destination is atomically
replaced after writing, flushing and a final cancellation check; errors before that point leave it intact. The
operation does not preserve filesystem attributes of a replaced destination. Streams need not be seekable and
remain open. Separate input/output streams are required; a write error or cancellation while copying the result
may leave partial output; use the path overloads when atomic replacement matters. Memory input returns a new byte
array. An edit that changes no block and no resolution leaves an in-place file untouched (no write, same
timestamps); a different destination receives an identical copy. Inputs and the prepared output are buffered;
this API avoids pixel buffers, but is not an incremental streaming editor.

The existing `DecoderOptions` select the configuration and cap encoded input bytes, total decompressed metadata,
canvas pixels and frame count. Limits apply to input and to edited metadata; dropping a block does not bypass the
input metadata limit. `IgnoreMetadata`, `FirstFrameOnly`, `TargetSize` and `ApplyExifOrientation` are unsupported.
Cancellation covers I/O and bounded CPU work outside the user callback; callbacks must handle their own work.
`Identify` keeps its existing lightweight behavior and does not start decompressing PNG metadata.

Container validation does not validate the compressed image bitstream by decoding it. Metadata-only PNG operations
always validate chunk CRCs. JPEG EXIF and standard XMP each need to fit in one APP1 segment and ICC in 255 APP2
segments; larger replacements throw `ArgumentException`, as in `Save`. Extended JPEG XMP in the input is
unsupported. JPEG MPF/MPO is rejected because its file offsets would need a separate rewrite. Other formats and format plugins without
an internal metadata adapter throw `NotSupportedException`.

Changing `HorizontalResolution`/`VerticalResolution` through the edit callback updates JPEG JFIF or PNG pHYs.
Editing `ExifTag.XResolution`/`YResolution` changes only EXIF. These are separate representations; applications
should update both if they need them synchronized. WebP has no container resolution field: edit its EXIF tags.

Synthetic little- and big-endian EXIF fixtures and their Pillow references can be regenerated with
`python3 -I tests/assets/metadata_reference.py`. Metadata Oracle tests use Pillow to check both edited tags
and every composited frame; separate tests compare the compressed coding/control payloads byte for byte.
