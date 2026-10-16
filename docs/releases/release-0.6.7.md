# PictureSharp 0.6.7

Faster HDR reconstruction, tone mapping and graphics operations through SIMD, with results that are identical on x64
and ARM64. The public API is unchanged.

- **HDR:** `image.ToHdr()` and `hdr.ToneMap()` use vector code (NEON on ARM64, SSE and AVX2 on x64). On a 12 MP photo
  with a quarter-resolution gain map (Intel i5-12500H), `ToHdr` went from 99 to 34 ms (mono map) and from 249 to 54 ms
  (RGB map with gamma, as on iPhone); on a single thread 663 → 137 ms and 1347 → 258 ms. `ToneMap` went from 87 to
  50 ms. On ARM64 (Orange Pi 5): `ToHdr` 156 → 100 ms and 534 → 195 ms, `ToneMap` 204 → 138 ms.
- **Reproducible HDR results:** the powers and exponentials of the gain map formulas now use PictureSharp's own `float`
  functions (fixed polynomials, within 2 ulp) instead of the platform's C runtime. `ToHdr` and `ToneMap` give
  bit-identical results on x64 and ARM64, with or without SIMD and for any thread count. Compared with 0.6.5 the
  floats differ by a few ulp (≤ 2·10⁻⁶ relative); tone-mapped images may differ by one level in rare pixels.
- **Graphics operations:** all twelve `DrawImage` blend modes (including `Normal`), `ApplyMask`, `AutoCrop` and
  `FindTrimBounds` have vector paths and give the same bytes as 0.6.5. At 12 MP on x64: `Multiply` 76 → 23 ms,
  `SoftLight` 99 → 33 ms, `Normal` 50 → 17 ms, alpha mask 23 → 8 ms, luminance mask 30 → 11 ms, `FindTrimBounds`
  2.3 → 1.3 ms. On ARM64 the masks are 4–5× and the blend modes about 2× faster (`Normal` 1.3×). Linear-light
  blending gains less (1.3–1.4×) because its table lookups stay scalar.
- **Unchanged:** `GetHistogram`, `Levels`, `Gamma`, `AutoContrast` and `Equalize` (a vector luma did not reach the 5%
  threshold), codecs and allocations.

Validation: Release build with zero warnings on linux-x64 and linux-arm64. The full test suite passes on both (x64:
5660 passed, 7 GDI+ tests skipped on Linux; ARM64 including the libultrahdr v2.0.2 oracle, now built there as well).
Every vector path is compared bit for bit with the scalar code (`*SimdTests`), also with `DOTNET_EnableHWIntrinsic=0`
and `DOTNET_EnableAVX2=0`. SHA-256 hashes of `ToHdr` floats and `ToneMap` bytes for iPhone and synthetic files, and of
CLI `convert -hdr tone-map` output, are identical on x64 and ARM64. The libultrahdr oracle and the double-precision
reference stay within their 0.6.5 tolerances. Measurements: BenchmarkDotNet 0.15.2 against release 0.6.5 on both
architectures; see [Performance](../performance.md#simd-for-hdr-and-graphics-067).

Regression check: the 80-case complete-operation matrix against 0.6.5 (ABBA, CPUs 0–7) has a median time change of
+0.24% and at most +0.29% allocations. Three codec cases above +5% (WebP lossy, JPEG and GIF encoding) come from code
that did not change in this release and were not investigated further. The trimmed and Native AOT package consumer
check runs in the release workflow before publishing.
