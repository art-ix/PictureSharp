# PictureSharp 0.6.5

HDR photos from iPhone (and Android Ultra HDR 1.1): JPEG files with a gain map are read and reconstructed in linear
light. Loading is unchanged unless `DecoderOptions.LoadGainMap` is set.

- **Loading:** with `LoadGainMap = true`, `Image.Load` keeps the same SDR base pixels and attaches the compressed gain
  map; `image.Hdr` (`HdrInfo`) and `image.HasHdr` describe it, `Image.Identify` fills `ImageInfo.Hdr` from the
  headers. ISO 21496-1 (iOS 18+, Ultra HDR 1.1) and Apple HDR Gain Map (MakerNote headroom, iOS 14–17) are
  supported; ISO wins when both are present. Damaged or unsupported HDR data never fails the load.
- **Reconstruction:** `image.ToHdr(HdrOptions?)` returns an `HdrImage` with `Rgba128` pixels (four floats, SDR white
  = 1, nothing clipped) in linear Display P3 or sRGB, with an optional linear `TargetHeadroom`. ISO reconstruction
  follows libultrahdr v2.0.2 (inverse-distance sampling, gamma, offsets, headroom weight, base or alternate color
  space, gray or RGB maps). `ApplyExifOrientation` and `TargetSize` work without a full-size map.
- **Tone mapping:** `hdr.ToneMap(ToneMappingOptions?)` returns an 8-bit sRGB image with a documented extended
  Reinhard operator (exposure, white point).
- **Rules:** `Mutate` and frame changes remove the map, `Clone()` keeps it; encoders never write HDR.
- **Metadata:** `ReadMetadata` and `EditMetadata` now accept JPEG files with a valid gain map: both compressed images
  are kept byte for byte and MPF offsets are updated; changing the ICC profile or removing data the map needs throws
  `InvalidOperationException`. Other MPO files are still rejected.
- **CLI:** `info` prints the gain map, `convert -hdr tone-map [-exposure] [-white]` renders HDR.

Validation: Release build with zero warnings; 5594/5594 tests on linux-x64 (including Oracle, Slow and local iPhone
samples); on ARM64 (Orange Pi 5) the full suite, Slow, Local, Oracle without libultrahdr and adapter tests pass, the
package consumer with trimming and Native AOT runs, and tone-mapped iPhone photos are byte-identical to x64. The
synthetic corpus is checked against an independent double-precision model (1e-5 relative); libultrahdr v2.0.2's
functions on identical pixels agree within the same tolerance, its full decoder within 0.42 % median. Plain JPEG
loading does no extra work without `LoadGainMap`.

Not yet verified: reconstruction against Apple ImageIO/Core Image on macOS (planned for the next release), BenchmarkDotNet
measurements. Maps whose aspect ratio differs by more than 1 % from the base use per-axis scaling instead of
libultrahdr's resize. See [HDR photos](../hdr.md).
