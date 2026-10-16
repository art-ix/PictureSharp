// Gain map reconstruction oracle built on libultrahdr v2.0.2 internals (gainmapmath.cpp), used by
// tests/assets/ultrahdr_compare.py to compare PictureSharp's math with the reference on identical decoded pixels,
// separately from libultrahdr's own JPEG decoding and YUV conversion.
//
// Build (scripts/build-ultrahdr-reference.sh does this):
//   g++ -O2 -std=c++17 -I reference/libultrahdr-2.0.2 -I reference/libultrahdr-2.0.2/lib/include \
//       tests/assets/ultrahdr_oracle.cpp reference/libultrahdr-2.0.2-build/libcore.a -ljpeg -lpthread -o ultrahdr_oracle
//
// Input (little-endian): int32 width, height, map width, map height, map channels (1 or 3); float32 headroom weight;
// float32 min_content_boost[3], max_content_boost[3], gamma[3], offset_sdr[3], offset_hdr[3], hdr_capacity_min,
// hdr_capacity_max; uint8 base RGB (sRGB-encoded) and uint8 map samples, row-major.
// Output: float32 linear RGB from the exact functions (srgbInvOetf, applyGain), then from the LUT functions the
// decoder uses by default (srgbInvOetfLUT, applyGainLUT). Sampling follows UltraHdr::applyGainMap: the table
// version of sampleMap for integer width ratios, the float version otherwise.
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <vector>

#include "ultrahdr/gainmapmath.h"

using namespace ultrahdr;

int main(int argc, char** argv) {
  if (argc != 3) {
    std::fprintf(stderr, "usage: ultrahdr_oracle input.bin output.f32\n");
    return 2;
  }
  FILE* in = std::fopen(argv[1], "rb");
  if (!in) return 1;
  int32_t header[5];
  float weight;
  float m[17];
  if (std::fread(header, sizeof header, 1, in) != 1 || std::fread(&weight, 4, 1, in) != 1 ||
      std::fread(m, sizeof m, 1, in) != 1)
    return 1;
  int w = header[0], h = header[1], mw = header[2], mh = header[3], channels = header[4];
  std::vector<uint8_t> base(size_t(w) * h * 3), map(size_t(mw) * mh * channels);
  if (std::fread(base.data(), 1, base.size(), in) != base.size() ||
      std::fread(map.data(), 1, map.size(), in) != map.size())
    return 1;
  std::fclose(in);

  uhdr_gainmap_metadata_ext_t metadata(kJpegrVersion);
  for (int i = 0; i < 3; i++) {
    metadata.min_content_boost[i] = m[i];
    metadata.max_content_boost[i] = m[3 + i];
    metadata.gamma[i] = m[6 + i];
    metadata.offset_sdr[i] = m[9 + i];
    metadata.offset_hdr[i] = m[12 + i];
  }
  metadata.hdr_capacity_min = m[15];
  metadata.hdr_capacity_max = m[16];
  metadata.use_base_cg = true;

  uhdr_raw_image_t gainmap{};
  gainmap.fmt = channels == 1 ? UHDR_IMG_FMT_8bppYCbCr400 : UHDR_IMG_FMT_24bppRGB888;
  gainmap.w = mw;
  gainmap.h = mh;
  gainmap.planes[0] = map.data();
  gainmap.stride[0] = mw;

  float map_scale_factor = (float)w / mw;
  int map_scale_factor_rnd = (std::max)(1, (int)std::roundf(map_scale_factor));
  ShepardsIDW idwTable(map_scale_factor_rnd);
  GainLUT gainLUT(&metadata, weight);

  std::vector<float> exact(size_t(w) * h * 3), lut(size_t(w) * h * 3);
  for (int y = 0; y < h; y++) {
    for (int x = 0; x < w; x++) {
      size_t i = (size_t(y) * w + x) * 3;
      Color gamma_sdr{{{base[i] / 255.0f, base[i + 1] / 255.0f, base[i + 2] / 255.0f}}};
      Color e_exact = srgbInvOetf(gamma_sdr), e_lut = srgbInvOetfLUT(gamma_sdr), hdr_exact, hdr_lut;
      bool integer = map_scale_factor == floorf(map_scale_factor);
      if (channels == 1) {
        float gain = integer ? sampleMap(&gainmap, map_scale_factor, x, y, idwTable)
                             : sampleMap(&gainmap, map_scale_factor, x, y);
        hdr_exact = applyGain(e_exact, gain, &metadata, weight);
        hdr_lut = applyGainLUT(e_lut, gain, gainLUT, &metadata);
      } else {
        Color gain = integer ? sampleMap3Channel(&gainmap, map_scale_factor, x, y, idwTable, false)
                             : sampleMap3Channel(&gainmap, map_scale_factor, x, y, false);
        hdr_exact = applyGain(e_exact, gain, &metadata, weight);
        hdr_lut = applyGainLUT(e_lut, gain, gainLUT, &metadata);
      }
      exact[i] = hdr_exact.r, exact[i + 1] = hdr_exact.g, exact[i + 2] = hdr_exact.b;
      lut[i] = hdr_lut.r, lut[i + 1] = hdr_lut.g, lut[i + 2] = hdr_lut.b;
    }
  }

  FILE* out = std::fopen(argv[2], "wb");
  if (!out) return 1;
  std::fwrite(exact.data(), 4, exact.size(), out);
  std::fwrite(lut.data(), 4, lut.size(), out);
  std::fclose(out);
  return 0;
}
