# Metadata

`image.Metadata` (`ImageMetadata`) carries the resolution and the raw ICC, EXIF and XMP blocks. Decoders fill it, processing keeps it, and encoders write it back.

```csharp
using var photo = Image.Load("photo.jpg");
ImageMetadata meta = photo.Metadata;
Console.WriteLine($"{meta.HorizontalResolution} × {meta.VerticalResolution} DPI, orientation {meta.Orientation}");
Console.WriteLine($"ICC: {meta.IccProfile?.Length ?? 0} bytes, EXIF: {meta.Exif?.Length ?? 0} bytes, XMP: {meta.Xmp?.Length ?? 0} bytes");

meta.HorizontalResolution = 300;          // must be positive and finite
meta.VerticalResolution = 300;
meta.Xmp = null;                          // drop one block
meta.Clear();                             // drop ICC, EXIF and XMP (the resolution stays)
```

| Member | Meaning |
| --- | --- |
| `HorizontalResolution`, `VerticalResolution` | DPI; 96 when the file does not specify it. |
| `IccProfile` | Raw ICC profile. Carried over, not applied: RasterSharp does no color management. |
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

Both reset the tag to `ExifOrientation.TopLeft`, so viewers do not rotate the image a second time.

## Choosing what to write

All encoder options have a `Metadata` property (`MetadataKinds` flags: `Icc`, `Exif`, `Xmp`, `All`, `None`):

```csharp
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
