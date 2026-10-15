# Metadata

`image.Metadata` (`ImageMetadata`) carries the resolution and the raw ICC, EXIF and XMP blocks. Decoders fill it, processing keeps it, and encoders write it back.

```csharp
using PictureSharp;
using PictureSharp.IO;

using var photo = ImageReader.Load("photo.jpg");
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

`ImageReader.Identify` also reports `Orientation` and the resolution without decoding pixels.

## EXIF orientation

Cameras often store photos sideways and record the rotation in EXIF. Make the pixels upright either while loading or later:

```csharp
using PictureSharp.IO;
using PictureSharp.Processing;

using var upright = ImageReader.Load("photo.jpg", new DecoderOptions { ApplyExifOrientation = true });

using var photo = ImageReader.Load("photo.jpg");
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
