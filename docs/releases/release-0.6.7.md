# PictureSharp 0.6.7

Faster HDR reconstruction, tone mapping and graphics operations through SIMD, with results that are identical on x64
and ARM64. The public API is unchanged.

- **HDR:** `image.ToHdr()` and `hdr.ToneMap()` use vector code (NEON on ARM64, SSE and AVX2 on x64).
- **Reproducible HDR results:** the powers and exponentials of the gain map formulas now use PictureSharp's own `float`
  functions (fixed polynomials, within 2 ulp) instead of the platform's C runtime. `ToHdr` and `ToneMap` give
  bit-identical results on x64 and ARM64, with or without SIMD and for any thread count. Compared with 0.6.5 the
  floats differ by a few ulp (≤ 2·10⁻⁶ relative); tone-mapped images may differ by one level in rare pixels.
- **Graphics operations:** all twelve `DrawImage` blend modes (including `Normal`), `ApplyMask`, `AutoCrop` and
  `FindTrimBounds` have vector paths and give the same bytes as 0.6.5. Linear-light blending gains less because its
  table lookups stay scalar.
- **Unchanged:** `GetHistogram`, `Levels`, `Gamma`, `AutoContrast` and `Equalize` (a vector luma did not reach the 5%
  threshold), codecs and allocations.

## Scalar (0.6.5) and SIMD (0.6.7)

BenchmarkDotNet 0.15.2, 12 MP (4000×3000), mean of 9 iterations. HDR inputs are JPEGs with an ISO 21496-1 gain map at
a quarter of the base resolution: a mono map, or an RGB map with gamma ≠ 1 as on iPhone.

### x64 (Intel i5-12500H)

| Operation | 0.6.5 scalar | 0.6.7 Vector128 | 0.6.7 AVX2 | Speed-up |
| --- | ---: | ---: | ---: | ---: |
| `ToHdr`, mono map | 99.5 ms | 39.6 ms | 34.3 ms | 2.9× |
| `ToHdr`, mono map, one thread | 663.3 ms | 169.7 ms | 136.8 ms | 4.8× |
| `ToHdr`, RGB map with gamma | 249.2 ms | 78.4 ms | 54.0 ms | 4.6× |
| `ToHdr`, RGB map with gamma, one thread | 1346.6 ms | 372.4 ms | 258.2 ms | 5.2× |
| `ToneMap` | 86.8 ms | 50.9 ms | 49.9 ms | 1.7× |
| `DrawImage` `Normal` | 49.8 ms | 29.3 ms | 17.1 ms | 2.9× |
| `DrawImage` `Multiply` | 76.3 ms | 35.9 ms | 23.2 ms | 3.3× |
| `DrawImage` `Screen` | 80.9 ms | 37.3 ms | 23.9 ms | 3.4× |
| `DrawImage` `SoftLight` | 99.5 ms | 58.2 ms | 33.2 ms | 3.0× |
| `DrawImage` `ColorBurn` | 98.6 ms | 50.3 ms | 31.0 ms | 3.2× |
| `DrawImage` `Multiply`, linear light | 84.8 ms | 67.6 ms | 63.4 ms | 1.3× |
| `ApplyMask` (alpha) | 23.1 ms | 8.2 ms | 8.0 ms | 2.9× |
| `ApplyMask` (luminance) | 30.2 ms | 10.5 ms | 10.7 ms | 2.8× |
| `FindTrimBounds` | 2.27 ms | 1.26 ms | 1.27 ms | 1.8× |
| `AutoCrop` (includes the crop copy) | 5.97 ms | 4.88 ms | 4.98 ms | 1.2× |

`ToneMap`, masks and trimming have no AVX2 form; their AVX2 column runs the Vector128 code.

### ARM64 (Orange Pi 5, RK3588)

| Operation | 0.6.5 scalar | 0.6.7 NEON | Speed-up |
| --- | ---: | ---: | ---: |
| `ToHdr`, mono map | 155.9 ms | 100.2 ms | 1.6× |
| `ToHdr`, mono map, one thread | 724.7 ms | 473.8 ms | 1.5× |
| `ToHdr`, RGB map with gamma | 533.6 ms | 195.2 ms | 2.7× |
| `ToHdr`, RGB map with gamma, one thread | 2560.7 ms | 987.1 ms | 2.6× |
| `ToneMap` | 204.3 ms | 137.9 ms | 1.5× |
| `DrawImage` `Normal`¹ | 129.5 ms | 100.4 ms | 1.3× |
| `DrawImage` `Multiply` | 207.3 ms | 104.1 ms | 2.0× |
| `DrawImage` `Screen` | 222.8 ms | 109.8 ms | 2.0× |
| `DrawImage` `SoftLight` | 292.8 ms | 155.1 ms | 1.9× |
| `DrawImage` `ColorBurn` | 292.0 ms | 133.5 ms | 2.2× |
| `DrawImage` `Multiply`, linear light | 252.3 ms | 182.0 ms | 1.4× |
| `ApplyMask` (alpha) | 94.1 ms | 18.6 ms | 5.1× |
| `ApplyMask` (luminance) | 127.5 ms | 31.7 ms | 4.0× |
| `FindTrimBounds` | 6.38 ms | 3.52 ms | 1.8× |
| `AutoCrop` | 10.94 ms | 8.48 ms | 1.3× |

¹ `graphics-probe` (median of seven samples) instead of BenchmarkDotNet.

## Validation

Release build with zero warnings on linux-x64 and linux-arm64. The full test suite passes on both (x64: 5660 passed,
7 GDI+ tests skipped on Linux; ARM64 including the libultrahdr v2.0.2 oracle, now built there as well). Every vector
path is compared bit for bit with the scalar code (`*SimdTests`), also with `DOTNET_EnableHWIntrinsic=0` and
`DOTNET_EnableAVX2=0`. SHA-256 hashes of `ToHdr` floats and `ToneMap` bytes for iPhone and synthetic files, and of
CLI `convert -hdr tone-map` output, are identical on x64 and ARM64. The libultrahdr oracle and the double-precision
reference stay within their 0.6.5 tolerances.

Regression check: the 80-case complete-operation matrix against 0.6.5 (ABBA, CPUs 0–7) has a median time change of
+0.24% and at most +0.29% allocations. Three codec cases above +5% (WebP lossy, JPEG and GIF encoding) come from code
that did not change in this release and were not investigated further. The trimmed and Native AOT package consumer
check passes in CI. See [Performance](../performance.md#simd-for-hdr-and-graphics-067).
