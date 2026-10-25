# Metadata

`image.Metadata` (`ImageMetadata`) carries the resolution, the color description and the raw ICC, EXIF and XMP blocks.
Decoders fill it, processing keeps it and encoders write it back.

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
| `IccProfile` | Raw ICC profile, written back; pixels are converted only on request ([Color management](color-management.md)). |
| `Exif` | Raw EXIF as a TIFF stream (`II*\0`/`MM\0*`); a JPEG `Exif\0\0` prefix is stripped. |
| `Xmp` | Raw XMP packet (UTF-8 XML). |
| `Gamma`, `Chromaticities`, `SrgbRenderingIntent` | PNG color description (`gAMA`, `cHRM`, `sRGB`), also used by plugins without ICC. |
| `Orientation` | The EXIF orientation tag (`ExifOrientation`); `Unknown` without EXIF. |
| `FormatMetadata` | Metadata only a plugin format understands, such as a cursor hotspot ([Format metadata](#format-metadata)). |
| `IsEmpty`, `Clear()`, `Clone()` | Whether ICC/EXIF/XMP and format metadata are absent; remove them; deep copy. |

## EXIF orientation

Cameras often store photos sideways and record the rotation in EXIF. Make the pixels upright while loading or later:

```csharp
using var upright = Image.Load("photo.jpg", new DecoderOptions { ApplyExifOrientation = true });

using var photo = Image.Load("photo.jpg");
photo.Mutate(x => x.AutoOrient());        // rotates/flips for orientations 2–8 and resets the tag to TopLeft
```

Both reset an existing tag to `ExifOrientation.TopLeft`, so viewers do not rotate twice; an absent tag stays absent.

## Choosing what to write

Every encoder options type has `Metadata` (`MetadataKinds` flags: `Icc`, `Exif`, `Xmp`, `All`, `None`):

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

JPEG EXIF and XMP must each fit in one APP1 segment (about 64 KB), otherwise `ArgumentException`. To skip reading the
blocks, load with `IgnoreMetadata = true`.

## Ownership and editing

`IccProfile`, `Exif` and `Xmp` copy on assignment and return independent snapshots; assign an edited block back or use
`SetBlock(kind, span)`. `GetBlockLength(kind)` and `CopyBlockTo(kind, destination)` read one block without allocating.
`RemoveBlocks(flags)` removes blocks and keeps DPI.

```csharp
var metadata = new ImageMetadata();
metadata.Orientation = ExifOrientation.RightTop; // creates missing EXIF/orientation
byte[] bytes = new byte[metadata.GetBlockLength(MetadataKinds.Exif)];
metadata.CopyBlockTo(MetadataKinds.Exif, bytes);
bool updated = metadata.TrySetOrientation(ExifOrientation.TopLeft);
metadata.RemoveBlocks(MetadataKinds.Xmp | MetadataKinds.Icc);
```

`Orientation` accepts 1–8, creates a missing tag and rejects malformed existing EXIF without changing it.
`TrySetOrientation` updates only a supported existing tag and returns false otherwise.

## Format metadata

Some formats store data that only they understand, such as the hotspot of an XBM image or a cursor. A plugin keeps it
in `ImageMetadata` as a record of its own type, at most one per type, so a load followed by a save in the same format
keeps it. Other formats ignore it: converting an XBM image to PNG drops the hotspot.

```csharp
// XbmMetadata is defined by the plugin; see its documentation for the records it provides.
if (image.Metadata.GetFormatMetadata<XbmMetadata>() is { Hotspot: { } hotspot })
    Console.WriteLine($"Hotspot at {hotspot.X}, {hotspot.Y}");

image.Metadata.SetFormatMetadata(new XbmMetadata(new Point(0, 0)));   // replaces the entry of that type
image.Metadata.RemoveFormatMetadata<XbmMetadata>();
```

- The library cannot move such data with the pixels, so operations that move pixels remove all of it: resize, crop,
  pad, rotate, flip, `AutoOrient` when it rotates, `AutoCrop`, a custom processor with another output size and
  `DecoderOptions.TargetSize`. Color and pixel operations keep it. Set it again after such an operation if it still
  applies.
- `DecoderOptions.IgnoreMetadata` skips it, and `Clear()` removes it; `RemoveBlocks` does not, as it is not a block.
- The records are immutable, so `Clone()` shares them and a list read from `FormatMetadata` does not change later.

## Typed EXIF

`ExifProfile` is a live view of the same bytes as `Exif`: edits through either API are immediately visible in the
other, and reading an empty profile creates nothing. A cloned `ImageMetadata` owns an independent profile.

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

- `ExifRational` keeps the exact unsigned numerator and denominator (`ToDouble()` converts); a zero denominator is
  rejected on write. Dates carry no offsets or fractional seconds; those tags stay preserved as opaque data.
- Missing tags and unsupported value shapes make `TryGetValue` return false. Malformed TIFF throws
  `ImageFormatException`, unsupported TIFF types `NotSupportedException`.
- Edits preserve unknown entries, MakerNotes and their offsets, and a failed edit leaves the block intact. `KeepOnly`
  rebuilds a small profile with only the requested tags, dropping thumbnails, unknown fields and MakerNotes.

### GPS position

`TryGetGpsLocation` and `SetGpsLocation` convert the degree/minute/second and hemisphere tags to a `GpsLocation` in
signed decimal degrees with an optional altitude in meters:

```csharp
using PictureSharp;

var metadata = new ImageMetadata();
metadata.ExifProfile.SetGpsLocation(new GpsLocation(-33.8688, 151.2093, altitude: 58));
if (metadata.ExifProfile.TryGetGpsLocation(out GpsLocation location))
    Console.WriteLine($"{location.Latitude:F4}, {location.Longitude:F4}, {location.Altitude} m");
bool removed = metadata.ExifProfile.RemoveGps(); // true: the GPS directory existed
```

Latitude must be in [−90, 90] and longitude in [−180, 180]. Seconds are stored to 1/10000″ (about 3 mm) and altitude to
millimeters; a `null` altitude removes the altitude tags while other GPS tags stay. Reading needs both coordinates and
hemispheres; incomplete or out-of-range values return false.

## Editing a file without recompression

`Image.ReadMetadata` reads JPEG, PNG/APNG or WebP metadata without decoding pixels, and `Image.EditMetadata` changes it
without re-encoding: every JPEG scan, PNG IDAT/fdAT and WebP image payload is copied byte for byte, with animation
controls and safe unknown chunks preserved. There is no fallback to `Load`/`Save`. Plugins can support both through
`IImageFormatModule.MetadataCodec`.

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

- **Overloads** follow `Load` (paths, streams, memory; options and cancellation). Memory overloads return a new array;
  `byte[]` binds to `ReadOnlyMemory<byte>` without copying.
- **Privacy.** `RemoveGps` handles standard EXIF GPS only, not MakerNotes or XMP. `Clear()` removes ICC/EXIF/XMP but
  keeps resolution and other container data such as IPTC or PNG text. Neither promises full anonymization. Changing
  orientation or ICC changes how viewers display the unchanged pixels.
- **Atomic paths.** Output goes to a temporary file in the destination directory and replaces the destination only
  after a successful write; filesystem attributes are not preserved and a symbolic link is replaced by a file. An edit
  that changes nothing leaves an in-place file untouched. Streams need not be seekable but must be separate, and may
  keep partial output after an error. Input and output are buffered in memory, without pixel buffers.
- **Limits.** `DecoderOptions` select the configuration and cap input bytes, decompressed metadata (input and edited),
  pixels and frames. `IgnoreMetadata`, `FirstFrameOnly`, `TargetSize` and `ApplyExifOrientation` are unsupported.
  Cancellation does not cover the user callback.
- **Validation.** The image bitstream is not decoded, PNG CRCs are always checked, oversized JPEG blocks throw
  `ArgumentException` and extended JPEG XMP is unsupported.
- **HDR.** JPEG files with a valid gain map keep both images with updated MPF offsets; edits changing the ICC profile or
  removing data the map needs throw `InvalidOperationException` (see [HDR photos](hdr.md)). Other MPF/MPO files, other
  built-in formats and plugins without a metadata codec throw `NotSupportedException`.
- **Resolution.** `HorizontalResolution`/`VerticalResolution` update JPEG JFIF or PNG pHYs; `ExifTag.XResolution`/
  `YResolution` change only EXIF. Update both if they must agree. WebP has only the EXIF tags.

