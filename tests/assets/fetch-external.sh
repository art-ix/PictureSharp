#!/usr/bin/env bash
# Downloads external conformance corpora (libwebp-test-data, PngSuite, Kodak, BMP Suite) into tests/assets/external (git-ignored).
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
if [ ! -d "$DEST/bmpsuite" ]; then
  # Jason Summers' BMP Suite: g/ (good), q/ (questionable), b/ (bad) images.
  tmp="$(mktemp -d)"
  curl -fsSL https://entropymine.com/jason/bmpsuite/releases/bmpsuite-2.8.zip -o "$tmp/bmpsuite.zip"
  unzip -q "$tmp/bmpsuite.zip" -d "$tmp/x"
  mkdir -p "$DEST/bmpsuite"
  for sub in g q b; do
    src="$(find "$tmp/x" -type d -name "$sub" | head -n 1)"
    [ -n "$src" ] && cp -r "$src" "$DEST/bmpsuite/$sub"
  done
  rm -rf "$tmp"
fi
echo "external corpora in $DEST"
