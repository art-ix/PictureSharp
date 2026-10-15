# PictureSharp 0.6.1

Typed EXIF and JPEG, PNG/APNG and WebP metadata editing without image recompression.
This version is prepared locally and has not been published; the latest published package remains 0.6.0.

- `Image.ReadMetadata` reads metadata without decoding pixels. `Image.EditMetadata` edits it through an
  `Action<ImageMetadata>` callback and copies compressed image and animation payloads byte for byte.
  There is no fallback to decoding and re-encoding. Paths, streams and memory are supported, with async file/stream I/O.
- `ImageMetadata.ExifProfile` shares one state with the raw `Exif` block. Typed `ExifTag<T>` values cover orientation,
  resolution, camera information, dates, exposure, aperture, ISO, focal length and basic GPS tags.
  `ExifRational` preserves exact fractions; EXIF dates use `DateTimeKind.Unspecified`.
- `SetValue`, `TryGetValue`, `RemoveValue` and `KeepOnly` provide typed editing. Unrelated opaque EXIF payloads retain
  their absolute offsets. Failed edits leave the original block intact. `KeepOnly` deliberately discards unknown fields,
  MakerNotes and thumbnails; `RemoveGps` removes standard EXIF GPS directories and their known data bytes.
- Path output is staged in a temporary file in the destination directory and atomically replaces the destination
  only after successful completion. In-place path edits are supported. Caller streams stay open and need not seek;
  a write error or cancellation can leave partial stream output. Input and prepared output are buffered in memory.
- Decoder input, metadata, pixel and frame limits apply, including decompressed PNG metadata and edited output blocks.
  Cancellation covers I/O and bounded library CPU work. Existing `Identify` behavior is unchanged.

`RemoveGps` does not inspect private MakerNotes or XMP. `Clear` removes the supported ICC/EXIF/XMP blocks while
retaining resolution and unrelated container information; these operations do not promise complete anonymization.
JPEG EXIF and standard XMP each must fit in one APP1 segment. Extended JPEG XMP and MPF/MPO are explicitly unsupported.
Changing orientation or ICC metadata may change viewer presentation even though compressed pixel data is unchanged.

Local validation: Release build with zero warnings; **4708/4708 full tests**, **155/155 Oracle** without skips,
**113/113 SIMD fallback tests** and **47/47 metadata tests without intrinsics**. The package consumer checks contents,
full trimming and linux-x64 Native AOT, including the new metadata APIs. The 80-case regression matrix and alternating
confirmation runs found no repeatable slowdown above 5%. Actual ARM64 execution remains a separate check.

See [metadata](metadata.md), [architecture](architecture.md#metadata-only-operations) and
[measurements](performance.md#metadata-without-image-recompression-061-implementation).
