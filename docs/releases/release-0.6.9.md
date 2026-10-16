# PictureSharp 0.6.9

HDR photos stay HDR when edited and saved as JPEG.

- **Gain maps follow geometric edits:** `Resize` (except padding), `Thumbnail`, `Crop`, `AutoOrient`, quarter-turn
  rotations and flips now edit the attached gain map with the base image instead of removing it, so `HasHdr` and
  `ToHdr` keep working after them. The map is decoded on the first edit and resampled in its own resolution; other
  operations still remove it. **Behavior change:** `image.Hdr` was null after any `Mutate` in 0.6.5–0.6.8.
- **Saving with the gain map:** `JpegEncoderOptions.GainMap` (`None`, `Preserve`, `Require`) writes the ISO 21496-1
  gain map as libultrahdr v2.0.2 does with its default settings: ISO metadata only and an MPF directory. The ISO
  metadata is copied unchanged. An unedited map is copied without re-encoding; an edited one is encoded with
  `GainMapQuality` (default 95). Apple-only maps are not written, and a user XMP packet that declares a gain map is left
  out. The default `Save` is byte for byte the same as in 0.6.8.
- **Plugins:** `ImageInfo.ContainerItemCount` lets container format plugins (such as the planned
  `PictureSharp.Heif`) report the number of items in a file; built-in formats leave it null.
- **CLI sample:** `convert`, `resize` and `thumbnail` take `-hdr keep` to write a JPEG with the gain map.

See [HDR photos](../hdr.md) for the table of operations that keep or remove the map and for the file layout.
Keeping the map costs 6–16% more time for `Load` → `Resize` → `Save` at 12 and 48 MP and no full-size float buffers
([measurements](../performance.md#editing-and-saving-hdr-photos-069)).

The adapter packages are released at the same version; their implementation is unchanged.

**Local validation (linux-x64):** 7,168 tests passed, with the Windows-only GDI+ tests skipped. The edited maps of 228
reference edits match the model in `tests/assets/hdr_reference.py` byte for byte; files written with the gain map
decode in libultrahdr's `ultrahdr_app` within the 0.6.5 pipeline budget. SIMD and HDR tests passed with AVX2 disabled
and with all intrinsics disabled. Local package consumers passed full trimming and Native AOT for the library and
adapters. Not tested on ARM64 or against Apple's ImageIO.
