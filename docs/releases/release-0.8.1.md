# PictureSharp 0.8.1

HDR images can be written: `hdr.ToGainMapImage()` computes an ISO 21496-1 gain map from an `HdrImage`, so HDR pixels
save as an Ultra HDR JPEG, and Apple-only gain maps are now written as ISO 21496-1. No breaking API changes since
0.8.0; files written from ISO sources are byte for byte the same.

- **`hdr.ToGainMapImage(options)`** returns an SDR image (the base, `hdr.ToneMap(options.ToneMapping)`) with an
  attached gain map computed as libultrahdr 2.0.2's `generateGainMap` computes it with its best-quality defaults:
  box-averaged samples, the log2 gain of HDR over SDR, bounds from the image's own gain range and an affine mapping to
  8-bit codes. The result carries the map like a loaded image, so `ToHdr`, geometric `Mutate` operations,
  `GetGainMap()` and JPEG with `JpegGainMapMode.Require` work as before. **`hdr.ToGainMapImage(sdrBase, options)`**
  uses a base of your own (same size, one frame, sRGB or Display P3 by its color description).

  ```csharp
  using HdrImage hdr = photo.ToHdr();
  using Image sdr = hdr.ToGainMapImage(new GainMapOptions { ToneMapping = new ToneMappingOptions { Exposure = -0.3f } });
  sdr.SaveAsJpeg("regraded.jpeg", new JpegEncoderOptions { Quality = 95, GainMap = JpegGainMapMode.Require });
  ```

- **`GainMapOptions`** (namespace `PictureSharp.Processing`): `Scale` (1–128, default 1), `ChannelCount` (3 or 1,
  default 3), `Gamma` (default 1), `Headroom` (default `hdr.Headroom`), `ToneMapping` and `MaxDegreeOfParallelism`.
  The defaults are libultrahdr's API defaults. When the HDR image is Display P3 and the base sRGB, the gain is applied
  in Display P3 and the map carries a Display P3 ICC profile, as libultrahdr writes it.
- **`new HdrImage(width, height, pixels, colorSpace, headroom, metadata)`** creates an `HdrImage` from a copy of
  linear `Rgba128` pixels, for example from a plugin decoding a floating-point format. `headroom` defaults to the
  largest component; `GainMapKind` is `None`.
- **Apple → ISO 21496-1:** `JpegGainMapMode.Preserve` and `Require` now write a map described by Apple metadata only,
  and `GetGainMap()` returns it, as ISO 21496-1: gain bounds 0 and log2 H, gamma 1, no offsets, one channel, with the
  map codes recomputed so that the full-headroom rendition matches Apple's formula on the ISO sampling grid. Before,
  `Preserve` wrote SDR, `Require` and `GetGainMap(require: true)` threw `NotSupportedException` and `GetGainMap()`
  returned null. `GainMapMetadata.ToIso()` on Apple metadata still throws, since the codes must change too. The CLI's
  `-hdr keep` accepts Apple-only files.

See [HDR files from HDR pixels](https://github.com/art-ix/PictureSharp/blob/v0.8.1/docs/hdr.md#hdr-files-from-hdr-pixels)
and [Floating-point HDR sources](https://github.com/art-ix/PictureSharp/blob/v0.8.1/docs/extending-picturesharp.md#floating-point-hdr-sources-081).

The adapter packages are released at the same version without changes.

**Validation** (budget agreed before the implementation, `generator` in `tests/assets/hdr_reference.json`):
- Against libultrahdr 2.0.2's `generateGainMap` on identical inputs (a new harness,
  `tests/assets/ultrahdr_generate_oracle.cpp`; synthetic and corpus HDR images, sRGB and Display P3, scales 1–4, one
  and three channels): map codes and ISO 21496-1 payloads are identical. The one deliberate difference: with gamma ≠ 1,
  gains below the clamped minimum give code 0, where libultrahdr computes `pow` of a negative number.
- Round trip `HdrImage` → JPEG (base quality 95 4:4:4, map 95) → `ToHdr`: on a smooth synthetic image a median error
  of 0.8% and p99 7% (map alone 0.2% / 0.7%); on iPhone photos 2.8% (sRGB) and 4.2% (Display P3) median. In every case
  at or below the same inputs encoded by libultrahdr's `ultrahdr_app`. Generated files decode in `ultrahdr_app` within
  the 0.6.5 pipeline budget.
- Apple → ISO at full headroom with the source's base pixels: median 0.2–0.5% and p99 4.5–10.6% on iPhone photos with
  the ISO segments hidden; median 0.6% and p99 10% on the 48×32 synthetic corpus. The written files decode in
  `ultrahdr_app` within the pipeline budget.
- All tests passed on linux-x64, including the oracle, slow and local tests, trimming and Native AOT (the smoke test
  generates a map and writes it to JPEG).
- No SIMD code was added; not run on ARM64.
