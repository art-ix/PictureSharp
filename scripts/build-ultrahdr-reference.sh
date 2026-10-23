#!/usr/bin/env bash
# Build the pinned ISO 21496-1 gain map oracle (libultrahdr) outside the managed library's build.
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
uhdr_source="$project_root/reference/libultrahdr-2.0.2"
uhdr_build="$project_root/reference/libultrahdr-2.0.2-build"
uhdr_commit=e5f5a022fe96fc4dc2ee35c19f733a50df807abe
if [[ ! -d "$uhdr_source" ]]; then
  git clone --depth 1 --branch v2.0.2 https://github.com/google/libultrahdr "$uhdr_source"
fi
if [[ "$(git -C "$uhdr_source" rev-parse HEAD)" != "$uhdr_commit" ]]; then
  echo "Expected libultrahdr v2.0.2 ($uhdr_commit) in $uhdr_source" >&2
  exit 1
fi
# The sample app (ultrahdr_app) is the oracle; optional HEIF/AVIF codecs and GPU paths stay off.
cmake -S "$uhdr_source" -B "$uhdr_build" -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DUHDR_BUILD_EXAMPLES=ON -DUHDR_BUILD_TESTS=OFF -DUHDR_BUILD_BENCHMARK=OFF -DUHDR_BUILD_FUZZERS=OFF \
  -DUHDR_ENABLE_GLES=OFF -DUHDR_BUILD_DEPS=OFF
cmake --build "$uhdr_build" --parallel "${PICTURESHARP_BUILD_JOBS:-4}"
# Math harness: libultrahdr's gain map functions on PictureSharp's decoded pixels (HdrUltraHdrOracleTests).
g++ -O2 -std=c++17 -I "$uhdr_source" -I "$uhdr_source/lib/include" "$project_root/tests/assets/ultrahdr_oracle.cpp" \
  "$uhdr_build/libuhdr.a" -ljpeg -lpthread -o "$uhdr_build/ultrahdr_oracle"
# ISO 21496-1 writer harness: fractions and encodeGainmapMetadata (HdrIsoWriterOracleTests).
g++ -O2 -std=c++17 -I "$uhdr_source" -I "$uhdr_source/lib/include" "$project_root/tests/assets/ultrahdr_iso_oracle.cpp" \
  "$uhdr_build/libuhdr.a" -ljpeg -lpthread -o "$uhdr_build/ultrahdr_iso_oracle"
"$uhdr_build/ultrahdr_app" 2>&1 | head -3 || true
echo "libultrahdr oracle ready in $uhdr_build"
