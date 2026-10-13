#!/usr/bin/env bash
# Build the pinned native oracle outside the managed library's build.
set -euo pipefail
project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
webp_source="$project_root/reference/libwebp-1.6.0"
webp_build="$project_root/reference/libwebp-1.6.0-build"
webp_commit=4fa21912338357f89e4fd51cf2368325b59e9bd9
if [[ ! -d "$webp_source" ]]; then
  git clone --depth 1 --branch v1.6.0 https://chromium.googlesource.com/webm/libwebp "$webp_source"
fi
if [[ "$(git -C "$webp_source" rev-parse HEAD)" != "$webp_commit" ]]; then
  echo "Expected libwebp v1.6.0 ($webp_commit) in $webp_source" >&2
  exit 1
fi
cmake -S "$webp_source" -B "$webp_build" -DCMAKE_BUILD_TYPE=Release \
  -DWEBP_BUILD_EXTRAS=OFF -DWEBP_BUILD_VWEBP=OFF
cmake --build "$webp_build" --parallel "${RASTERSHARP_BUILD_JOBS:-4}"
for webp_tool in cwebp dwebp webpmux img2webp; do
  "$webp_build/$webp_tool" -version
done
echo "Reference tools ready in $webp_build"
echo 'Tests and corpus scripts select this directory automatically.'
