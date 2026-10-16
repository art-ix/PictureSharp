# PictureSharp 0.6.7

SIMD for HDR and graphics operations. No API changes.

- **HDR:** `ToHdr` and `ToneMap` use NEON (ARM64) and SSE/AVX2 (x64). Gain map math now uses PictureSharp's own
  deterministic `float` functions, so results are bit-identical on x64 and ARM64; they differ from 0.6.5 by a few ulp
  (≤ 2·10⁻⁶ relative).
- **Graphics:** all `DrawImage` blend modes (including `Normal`), `ApplyMask` and `AutoCrop`/`FindTrimBounds` are
  vectorized and give the same bytes as 0.6.5.

## Scalar (0.6.5) vs SIMD (0.6.7)

BenchmarkDotNet, 12 MP; HDR with a quarter-resolution ISO gain map (RGB map with gamma, as on iPhone).

| Operation | x64 0.6.5 | x64 0.6.7 | ARM64 0.6.5 | ARM64 0.6.7 |
| --- | ---: | ---: | ---: | ---: |
| `ToHdr` | 249 ms | 54 ms (4.6×) | 534 ms | 195 ms (2.7×) |
| `ToHdr`, one thread | 1347 ms | 258 ms (5.2×) | 2561 ms | 987 ms (2.6×) |
| `ToneMap` | 87 ms | 50 ms (1.7×) | 204 ms | 138 ms (1.5×) |
| `DrawImage` `Normal` | 50 ms | 17 ms (2.9×) | 130 ms | 100 ms (1.3×) |
| `DrawImage` `Multiply` | 76 ms | 23 ms (3.3×) | 207 ms | 104 ms (2.0×) |
| `DrawImage` `SoftLight` | 99 ms | 33 ms (3.0×) | 293 ms | 155 ms (1.9×) |
| `ApplyMask` (alpha) | 23 ms | 8 ms (2.9×) | 94 ms | 19 ms (5.1×) |
| `FindTrimBounds` | 2.3 ms | 1.3 ms (1.8×) | 6.4 ms | 3.5 ms (1.8×) |

x64: Intel i5-12500H with AVX2; ARM64: Orange Pi 5 (RK3588). Linear-light blending gains only 1.3–1.4× (table
lookups stay scalar). Full results: [Performance](../performance.md#simd-for-hdr-and-graphics-067).

**Validation:** all tests pass on x64 and ARM64; every vector path is tested bit-identical to the scalar code (also
without intrinsics and without AVX2); HDR output hashes match on x64 and ARM64. The 80-case regression matrix against
0.6.5: median +0.24%, allocations unchanged.
