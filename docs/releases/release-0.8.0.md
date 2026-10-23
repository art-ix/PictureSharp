# PictureSharp 0.8.0

Format plugins can write HDR gain maps: an encoder reads the map attached to the image, and the JPEG encoder now also
writes maps that a plugin attached, so an HEIC photo loaded through a plugin can be saved as an Ultra HDR JPEG. No
breaking API changes since 0.7.13; files written from JPEG sources are byte for byte the same.

- **`image.GetGainMap()`** (namespace `PictureSharp.Formats`) returns the map an encoder writes as an
  `EncodableGainMap`: its `GainMapMetadata` and a `GainMapImage` in the size and orientation of the image's current
  pixels, after `ApplyExifOrientation` and geometric edits. When the map was attached by a plugin and the image was
  neither rotated nor edited, `Map` is the plugin's own instance, so the plugin can copy its compressed data instead of
  encoding it again. `GetGainMap()` decodes nothing; `Map.Decode` returns a new image on every call.
  `require: true` throws what `JpegGainMapMode.Require` throws; otherwise a missing, Apple-only or unusable map gives
  `null`.

  ```csharp
  if (image.GetGainMap(require: options.GainMap == MyGainMapMode.Require) is { } gainMap)
  {
      byte[] iso = gainMap.Metadata.ToIso();
      using Image map = gainMap.Map.Decode(maxDegreeOfParallelism: -1, cancellationToken);
  }
  ```

- **`GainMapMetadata.ToIso()`** writes ISO 21496-1 metadata, the input of `ParseIso`: the payload of a JPEG APP2
  segment or a HEIF `tmap` item. Values are stored as the fractions libultrahdr 2.0.2 chooses (a port of
  `encodeGainmapMetadata` and its continued fractions), one channel when all channels are equal.
  **`GainMapMetadata.MultiplicationSpace`** gives the primaries the gain is applied in when they are not the base
  image's; the payload only flags them, so a writer stores them with the map (an ICC profile or a `colr` box).
- **JPEG with a plugin's map:** `JpegGainMapMode.Preserve` and `Require` write a map attached by a format plugin like
  an edited map (`GainMapQuality`, gray maps as grayscale, RGB maps 4:4:4) with metadata from `ToIso()`. Before, they
  wrote SDR or threw `NotSupportedException`. When the gain is applied in primaries other than the base image's, the
  map gets the ICC profile libultrahdr writes for sRGB or Display P3. Maps loaded from JPEG are still copied with
  their original metadata.

See [Writing gain maps in a plugin](https://github.com/art-ix/PictureSharp/blob/v0.8.0/docs/extending-picturesharp.md#hdr-gain-maps)
and [HDR](https://github.com/art-ix/PictureSharp/blob/v0.8.0/docs/hdr.md).

The adapter packages are released at the same version without changes.

**Validation:**
- Against libultrahdr 2.0.2 (a new harness, `tests/assets/ultrahdr_iso_oracle.cpp`): the fractions of about 16,000
  values and the ISO payloads of more than 500 metadata sets (corpus and random, one and three channels, both
  layouts) and the sRGB and Display P3 map profiles are byte for byte identical. `ParseIso(ToIso(m))` gives the same
  values for every ISO file of the corpus.
- JPEG files written from plugin maps decode in `ultrahdr_app` within the 0.6.5 pipeline budget; reloaded, their
  metadata is exact and the map within 8 codes of the attached one (JPEG quality 95, the 0.6.9 budget for edited
  maps).
- A test plugin writes maps through `GetGainMap`: JPEG to plugin and plugin to plugin round trips, unedited plugin
  maps copied byte for byte, oriented and edited maps in the geometry of the base pixels.
- 75 JPEG files written with `Preserve` from the HDR corpus (unedited, oriented, edited) are byte for byte those of
  0.7.13.
- All tests passed on linux-x64, including the oracle and slow tests, trimming and Native AOT (the smoke test reads a
  plugin's map with `GetGainMap` and writes it to JPEG).
- No SIMD code was added; not run on ARM64.
