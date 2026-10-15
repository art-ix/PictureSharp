# Layered refactoring verification — 2026-10-15

Release note: these measurements were captured before publication; the verified changes are included in 0.5.5.

Baseline: commit `ca427ea66af671588dd540d008ac567afcb1a993`, published 0.5.4. Refactored code: local working tree; version unchanged. No commit, push or publication.

## Correctness and packaging

- Release solution build: zero warnings and errors.
- Complete regression suite: **5081/5081**, including five architecture tests (baseline: 5076/5076).
- Oracle suite: **150/150**, using installed reference tools.
- SIMD tests with `DOTNET_EnableHWIntrinsic=0`: **113/113**.
- Updated public API snapshot and compiled documentation examples pass. CLI and benchmark projects build.
- Full trimming and Native AOT linux-x64 consumers both build and execute successfully. Smoke coverage: seven formats, fluent processing, async streams, exact PNG pixels, independent ICO variants, PNG animation and reduced JPEG thumbnails.
- Local `.nupkg` and `.snupkg` created. Main package includes DLL/XML docs, README, LICENSE and third-party notices; no package dependencies or native runtime assets.
- A separate consumer restored PictureSharp **from the local package feed in an isolated package cache**, then built and ran with Native AOT. This verifies the actual package rather than only a project reference.
- ARM64 runtime verification was not performed; only x64 hardware was available.

## Performance

Machine: Intel Core i5-12500H, Linux x64, .NET SDK 10.0.112/runtime 10.0.12. Both versions used CPU affinity `0-7` (the same performance cores and SMT threads); no custom local-photo inputs.

The existing `MatrixBenchmarks` supplies **80 cases**: four committed/derived inputs × twenty complete encode/decode/resize operations. An external driver invoked the existing setup/run/cleanup methods against preserved baseline and refactored binaries. Each case used two warm-up calls and seven timed samples of at least 120 ms, recording the median time and process-wide allocated bytes per operation. GC runs between samples. Full sweeps used baseline → refactored → refactored → baseline order. This lightweight regression probe is distinct from a full BenchmarkDotNet statistical run. The equivalent current-tree driver is available through the benchmark project's `matrix-probe` command.

Median of the eighty per-case changes across complete sweeps: **-0.79%**. Allocated-byte differences were below 0.3%; no hot-loop allocations were introduced. Allocation regression tests passed.

Three full-sweep aggregates exceeded +5%, driven by run-to-run variation. They were rechecked by loading both preserved versions into separate AssemblyLoadContexts **in one process**, using 500 ms warm-up per version, calibrated equal iteration counts, fifteen samples and alternating measurement order. Delegates invoke the unchanged matrix methods; shared runtime conditions reduce process and scheduling differences.

| Case | Paired confirmation |
| --- | ---: |
| photo.png / BmpDecode | -1.43% |
| photo.png / ResizeLanczosHalf | +0.85% |
| graphic.png#8x / WebPLosslessDecode1T | -0.00% |

No repeatable regression above **5%** was observed. The largest remaining unflagged full-matrix aggregate was +4.43%. Do not interpret these laptop measurements as a guaranteed speedup.

Full sweep times, allocation medians and confirmation values are in [architecture-benchmarks.csv](architecture-benchmarks.csv). The original 80-case data is retained in that table; paired confirmations are separate columns rather than overwriting outliers.

Reproduce the current-tree probe:

```bash
taskset -c 0-7 dotnet run -c Release --project benchmarks/PictureSharp.Benchmarks -- matrix-probe /tmp/picturesharp-matrix.json
```

For publication-quality timing use the existing BenchmarkDotNet runner and compare equivalent builds on the same machine.

A one-off textual comparison after removing imports, namespaces, comments and formatting confirmed unchanged executable text in **182 algorithm/helper/codec files**. Remaining edits concern orchestration boundaries, shared-type extraction and the JPEG decode result. Regression/oracle tests verify behavior across those boundaries.

## Follow-up: stronger domain and C# 14

The subsequent domain hardening and previous extension foundations are verified separately in [DDD verification](ddd-verification.md): 5134 tests, Oracle 150, scalar fallbacks 113, local NuGet consumer under full trimming and Native AOT, and the 80-case matrix with paired follow-ups. The earlier results above describe the first structural refactor.
