#!/usr/bin/env bash
# Downloads external conformance corpora into tests/assets/external (git-ignored).
# Requires network access; run only with the maintainer's consent.
set -euo pipefail
DEST="$(cd "$(dirname "$0")" && pwd)/external"
mkdir -p "$DEST"

if [ ! -d "$DEST/libwebp-test-data" ]; then
  git clone --depth 1 https://chromium.googlesource.com/webm/libwebp-test-data "$DEST/libwebp-test-data"
fi

if [ ! -d "$DEST/pngsuite" ]; then
  mkdir -p "$DEST/pngsuite"
  curl -fsSL http://www.schaik.com/pngsuite/PngSuite-2017jul19.tgz | tar -xz -C "$DEST/pngsuite"
fi

if [ ! -d "$DEST/kodak" ]; then
  mkdir -p "$DEST/kodak"
  for i in $(seq -w 1 24); do
    curl -fsSL "http://r0k.us/graphics/kodak/kodak/kodim$i.png" -o "$DEST/kodak/kodim$i.png"
  done
fi
echo "external corpora in $DEST"
