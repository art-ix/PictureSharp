// Gain map generation oracle built on libultrahdr v2.0.2 (jpegr.cpp UltraHdr::generateGainMap with the API's
// default best-quality preset, gainmapmetadata.cpp encodeGainmapMetadata), used by HdrGainMapGeneratorOracleTests to
// compare PictureSharp's generator with the reference on identical inputs, separately from JPEG compression.
//
// Build (scripts/build-ultrahdr-reference.sh does this):
//   g++ -O2 -std=c++17 -I reference/libultrahdr-2.0.2 -I reference/libultrahdr-2.0.2/lib/include \
//       tests/assets/ultrahdr_generate_oracle.cpp reference/libultrahdr-2.0.2-build/libuhdr.a -ljpeg -lpthread \
//       -o ultrahdr_generate_oracle
//
// Input (little-endian): int32 width, height, scale, channels (1 or 3), SDR gamut, HDR gamut (0 = BT.709, 1 = Display
// P3); float32 gamma, headroom (linear); uint8 SDR RGBA (sRGB transfer); uint16 HDR RGBA half floats (linear,
// 1 = 203 nits), row-major.
// Output: int32 map width, height, channels; uint8 map codes (row-major, interleaved); uint8 use_base_cg; float32
// min_content_boost[3], max_content_boost[3]; int32 ISO payload length and the payload (gainmapMetadataFloatToFraction
// and encodeGainmapMetadata). hdr_capacity_max is set to the given headroom instead of targetDispPeakBrightness / 203,
// which may differ from it in the last bit.
#include <cfloat>
#include <cstdint>
#include <cstdio>
#include <memory>
#include <vector>

#include "ultrahdr/gainmapmath.h"
#include "ultrahdr/gainmapmetadata.h"
#include "ultrahdr/jpegr.h"

using namespace ultrahdr;

int main(int argc, char** argv) {
  if (argc != 3) {
    std::fprintf(stderr, "usage: ultrahdr_generate_oracle input.bin output.bin\n");
    return 2;
  }
  FILE* in = std::fopen(argv[1], "rb");
  if (!in) return 1;
  int32_t header[6];
  float settings[2];
  if (std::fread(header, sizeof header, 1, in) != 1 || std::fread(settings, sizeof settings, 1, in) != 1) return 1;
  int w = header[0], h = header[1], scale = header[2], channels = header[3];
  std::vector<uint32_t> sdr(size_t(w) * h);
  std::vector<uint64_t> hdr(size_t(w) * h);
  if (std::fread(sdr.data(), 4, sdr.size(), in) != sdr.size() || std::fread(hdr.data(), 8, hdr.size(), in) != hdr.size())
    return 1;
  std::fclose(in);

  uhdr_raw_image_t sdr_intent{};
  sdr_intent.fmt = UHDR_IMG_FMT_32bppRGBA8888;
  sdr_intent.cg = (uhdr_color_gamut_t)header[4];
  sdr_intent.ct = UHDR_CT_SRGB;
  sdr_intent.range = UHDR_CR_FULL_RANGE;
  sdr_intent.w = w;
  sdr_intent.h = h;
  sdr_intent.planes[UHDR_PLANE_PACKED] = sdr.data();
  sdr_intent.stride[UHDR_PLANE_PACKED] = w;

  uhdr_raw_image_t hdr_intent{};
  hdr_intent.fmt = UHDR_IMG_FMT_64bppRGBAHalfFloat;
  hdr_intent.cg = (uhdr_color_gamut_t)header[5];
  hdr_intent.ct = UHDR_CT_LINEAR;
  hdr_intent.range = UHDR_CR_FULL_RANGE;
  hdr_intent.w = w;
  hdr_intent.h = h;
  hdr_intent.planes[UHDR_PLANE_PACKED] = hdr.data();
  hdr_intent.stride[UHDR_PLANE_PACKED] = w;

  float gamma = settings[0], headroom = settings[1];
  JpegR jpegr(nullptr, scale, 95, channels == 3, gamma, UHDR_USAGE_BEST_QUALITY, FLT_MIN, FLT_MAX,
              headroom * kSdrWhiteNits);
  uhdr_gainmap_metadata_ext_t metadata(kJpegrVersion);
  std::unique_ptr<uhdr_raw_image_ext_t> gainmap;
  uhdr_error_info_t status = jpegr.generateGainMap(&sdr_intent, &hdr_intent, &metadata, gainmap);
  if (status.error_code != UHDR_CODEC_OK) {
    std::fprintf(stderr, "generateGainMap: %s\n", status.has_detail ? status.detail : "error");
    return 1;
  }
  metadata.hdr_capacity_max = headroom;

  uhdr_gainmap_metadata_frac frac{};
  std::vector<uint8_t> payload;
  if (uhdr_gainmap_metadata_frac::gainmapMetadataFloatToFraction(&metadata, &frac).error_code != UHDR_CODEC_OK ||
      uhdr_gainmap_metadata_frac::encodeGainmapMetadata(&frac, payload).error_code != UHDR_CODEC_OK)
    return 1;

  FILE* out = std::fopen(argv[2], "wb");
  if (!out) return 1;
  int32_t size[3] = {(int32_t)gainmap->w, (int32_t)gainmap->h, channels};
  std::fwrite(size, sizeof size, 1, out);
  const uint8_t* codes = static_cast<const uint8_t*>(gainmap->planes[UHDR_PLANE_PACKED]);
  for (unsigned y = 0; y < gainmap->h; y++) {
    std::fwrite(codes + size_t(y) * gainmap->stride[UHDR_PLANE_PACKED] * channels, 1, size_t(gainmap->w) * channels, out);
  }
  uint8_t useBase = metadata.use_base_cg ? 1 : 0;
  std::fwrite(&useBase, 1, 1, out);
  std::fwrite(metadata.min_content_boost, 4, 3, out);
  std::fwrite(metadata.max_content_boost, 4, 3, out);
  int32_t length = (int32_t)payload.size();
  std::fwrite(&length, 4, 1, out);
  std::fwrite(payload.data(), 1, payload.size(), out);
  std::fclose(out);
  return 0;
}
