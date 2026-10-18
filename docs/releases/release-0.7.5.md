# PictureSharp 0.7.5

A maintenance release that makes lossy WebP and the common load → orient → convert → resize → save path faster. No
public API changes; decoded pixels, encoded bytes and processing results are identical to 0.7.4, also without AVX2
and without hardware intrinsics, with one intended exception described below.

- **WebP lossy encoding:** the encoder's DSP now follows libwebp's SSE2/SSE4.1 code in 16-bit lanes (forward
  transforms, also two blocks at once, quantization, distortion metrics, histogram collection and the inverse
  transform where its input range allows it) and builds its intra predictions with vector code. The token buffer is
  one pooled array, so a 12 MP encode allocates 4.7 MB instead of 30 MB. On one thread Kodak `kodim23` encodes in
  31.3 ms instead of 45.6 ms and a 12 MP photo in 1.21 s instead of 1.68 s (1.36× and 1.54× libwebp 1.6.0).
- **WebP lossy decoding:** coefficient parsing keeps the arithmetic decoder's state in registers and reconstruction
  copies blocks without per-call overhead. Decoding is 8–11% faster: a 12 MP photo in 130 ms instead of 142 ms on one
  thread (1.29× libwebp).
- **Processing pipeline:** quarter-turn rotations (`AutoOrient`, `Rotate`, `ApplyExifOrientation`) work in cache-sized
  tiles, 12 MP in about 12 ms instead of 93 ms; `ConvertToSrgb` converts bands in parallel, and in place while an image
  is decoded with `ColorConversion = ToSrgb`; linear-light resizing works in bands of output rows instead of
  image-sized float buffers (about 340 MB pooled for 12 MP before) and is faster. Loading a 12 MP JPEG with EXIF
  orientation and a Display P3 profile, resizing it to 1600 px and saving WebP takes 210–215 ms instead of 355 ms and
  allocates 106 MiB instead of 153 MiB; a 256 px thumbnail with color conversion allocates 3.4 MiB instead of 6.2 MiB.

**Fixed:** multi-threaded lossy WebP encoding (method 3 and above, without `AutoFilter`) could, rarely, produce
different bytes from run to run: workers updated a per-segment maximum without synchronization, which could lower the
loop-filter strength. About 1% of method 6 encodes of a photo were affected in 0.7.4. Output now always equals the
single-threaded result and `cwebp` 1.6.0.

The single-thread goals of this release were 1.5× libwebp for encoding and 1.3× for decoding. Encoding meets it on
Kodak images and misses it slightly on a 12 MP photo (1.54×); decoding meets it on 12 MP photos (1.29×), while small
and low-bitrate photos such as `kodim23` stay at about 1.6×, where prediction and the loop filter dominate. See
[Performance](../performance.md#webp-lossy-and-pipeline-optimization-075).

The adapter packages are released at the same version without changes.

**Validation:** all tests passed on linux-x64 (including oracle runs, slow tests, runs without AVX2 and without
hardware intrinsics). About 2,800 outputs (encoder options, decoder corpus with truncated and corrupt files, pipeline
operations) were compared with the 0.7.4 code in all three modes. New vector code uses portable `Vector128` paths that
also run on ARM64, but ARM64 was not run for this release.
