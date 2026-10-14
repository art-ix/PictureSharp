# Performance

## VP8L predictors in 0.4.3

Release 0.4.3 adds SSE2 and AVX2 reconstruction for lossless WebP predictor modes 10 (nested averages), 11 (selection) and 12 (clamped addition/subtraction). The routines are ported from libwebp 1.6.0. Pixels still depend on the reconstructed pixel to their left: AVX2 prepares eight pixels, then carries that dependency through the two 128-bit halves. Short tails and CPUs without SSE2 use scalar code.

This release does not optimize Huffman decoding, encoder predictor search, PNG, resizing or ARM. Modes 5–7 and 13 remain scalar. Results must match the scalar implementation exactly.

### Predictor measurements

Measured on 2026-10-14: Intel Core i5-12500H, Linux x64, .NET SDK 10.0.112, runtime 10.0.12, BenchmarkDotNet 0.15.2, workstation GC. Tiered compilation was disabled for the BenchmarkDotNet runs so short, single-invocation iterations did not measure transitions between JIT tiers.

The input is a deterministic 1920×1440 array of residuals (seed 43). Buffers are reset outside the timed operation. Mixed mode cycles through 10, 11 and 12 across tiles. Numbers are mean ± standard deviation, in milliseconds, for 32-pixel tiles (`Bits=5`):

| Predictor | Scalar | Automatic SIMD | Speedup |
| --- | ---: | ---: | ---: |
| 10 | 11.122 ± 0.082 | 5.162 ± 0.075 | 2.15× |
| 11 | 73.534 ± 0.369 | 6.211 ± 0.130 | 11.84× |
| 12 | 35.502 ± 0.277 | 4.245 ± 0.080 | 8.36× |
| Mixed | 40.286 ± 0.309 | 5.236 ± 0.088 | 7.69× |

All 24 cases (four mode settings × three tile sizes × two implementations) were measured. For tile sizes 4/8/32, speedups were 1.75–2.15× for mode 10, 11.47–11.84× for mode 11, 6.86–8.36× for mode 12 and 7.13–7.69× for mixed modes. Both implementations allocated zero bytes during reconstruction. Separate runs with AVX2 disabled confirmed improvements over scalar for all three modes using SSE2.

These are transform timings, not complete image decoding timings. In particular, a file that uses other predictors or spends most of its time decoding Huffman symbols will gain less.

### Complete decoding

The before/after comparison used a saved copy of the initial 0.4.2 working-tree library, including its pre-existing changes, and the same encoded bytes after adding the new predictors. The large inputs are lossless WebP files generated once from a local photograph, with `Exact=true`, method 4 and quality 75. They are not committed. Each batch used eight warmups and 21 measured calls to `Image.Load` from a byte array, with default tiered compilation. The table shows medians from two independent batches:

| Image | Before, batches 1 / 2 | After, batches 1 / 2 | Reduction |
| --- | ---: | ---: | ---: |
| 1920×1440 | 49.22 / 53.75 ms | 48.48 / 49.92 ms | 1.5% / 7.1% |
| 4000×3000 | 208.38 / 206.44 ms | 193.72 / 186.66 ms | 7.0% / 9.6% |

For the second batch, the 10th–90th percentile intervals were 50.92–57.24 ms before and 46.90–52.44 ms after for 1920×1440, and 204.41–215.11 ms before and 183.07–189.45 ms after for 4000×3000. Treat the smaller-image improvement as modest: its intervals overlap. The photo, graphic and alpha-photo corpus inputs showed no repeatable regression above 3%. Allocations for full decoding were unchanged (about 21.6 MiB and 92.7 MiB per large image); predictor reconstruction itself adds no allocations.

A separate BenchmarkDotNet run with tiered compilation disabled measured the new decoder at 53.70 ± 0.26 ms / 21.63 MiB for 1920×1440 and 215.54 ± 0.62 ms / 92.67 MiB for 4000×3000. These figures include the complete library API and GC costs and should not be substituted for the default-JIT before/after measurements above.

On the same bytes, `dwebp -v` 1.6.0 reported median internal decode times of 47 ms and 190 ms (five warmups, 21 samples; 10th–90th percentiles 45–52 ms and 187–194 ms). That timer excludes file reading, process startup and output writing, has millisecond precision, and does not measure the same API overhead as RasterSharp. The BenchmarkDotNet/API times are about 1.14× / 1.13× those native internal times on these two inputs; this is context, not a guarantee for other files or machines.

## Running the benchmarks

Run from the repository root. The predictor benchmark has an iteration setup to restore the input; keep one invocation per iteration so previously reconstructed pixels are never reused as residuals:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessPredictorBenchmarks*' --launchCount 1 --warmupCount 5 \
  --iterationCount 12 --invocationCount 1 --unrollFactor 1
```

The iterations are deliberately short for this focused comparison. BenchmarkDotNet reports confidence intervals and warnings; use more launches/iterations when investigating small differences. Repeat with `DOTNET_EnableAVX2=0` to exercise SSE2, or `DOTNET_EnableHWIntrinsic=0` to exercise scalar fallbacks. For production-style tiered-JIT measurements, omit `DOTNET_TieredCompilation=0` and provide substantially longer warmup runs.

The decoder benchmark reads each file once during setup, checks that it is a static lossless WebP, decodes from memory, and disposes each image. The committed photo, graphic and alpha-photo inputs work without reference tools:

```bash
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
```

Add local inputs through `RASTERSHARP_BENCHMARK_WEBP_FILES`: paths separated by the platform's path separator (`:` on Linux/macOS, `;` on Windows). Use the same encoded files for before/after comparisons; do not re-encode separately for each version. Local photos and benchmark artifacts remain ignored by Git.

```bash
RASTERSHARP_BENCHMARK_WEBP_FILES='/absolute/photo-2.8mp.webp:/absolute/photo-12mp.webp' \
DOTNET_TieredCompilation=0 dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks -- \
  --filter '*LosslessDecodeBenchmarks*' --launchCount 1 --warmupCount 3 \
  --iterationCount 8 --iterationTime 100
reference/libwebp-1.6.0-build/dwebp -v /absolute/photo-12mp.webp
```

Keep CPU, runtime, GC settings, input bytes and workload identical when comparing versions. Measure serially, inspect the spread, and repeat any apparent regression. BenchmarkDotNet writes timing and allocation reports to `BenchmarkDotNet.Artifacts/`.
