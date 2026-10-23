// ISO 21496-1 metadata writer oracle built on libultrahdr v2.0.2 (gainmapmath.cpp floatTo*Fraction,
// gainmapmetadata.cpp encodeGainmapMetadata), used by HdrIsoWriterOracleTests.
//
// Build (scripts/build-ultrahdr-reference.sh does this):
//   g++ -O2 -std=c++17 -I reference/libultrahdr-2.0.2 -I reference/libultrahdr-2.0.2/lib/include \
//       tests/assets/ultrahdr_iso_oracle.cpp reference/libultrahdr-2.0.2-build/libuhdr.a -ljpeg -lpthread -o ultrahdr_iso_oracle
//
// Usage: ultrahdr_iso_oracle fractions|iso|icc input.bin output.bin (little-endian records; icc ignores the input).
// icc: per gamut (BT.709, Display P3) int32 length and the ICC profile IccHelper::writeIccProfile writes for a gain
//   map with the sRGB transfer, without the JPEG APP2 identifier.
// fractions: input float32 values; output per value uint8 ok, uint32 numerator, uint32 denominator from
//   floatToUnsignedFraction, then uint8 ok, int32 numerator, uint32 denominator from floatToSignedFraction.
// iso: input records of uint8 use_base_colour_space and float32 log2 max[3], log2 min[3], gamma[3], base offset[3],
//   alternate offset[3], log2 base headroom, log2 alternate headroom; output per record int32 payload length (-1 when a
//   value has no fraction) and the payload. The conversion follows gainmapMetadataFloatToFraction, except that the
//   log2 values are taken as given instead of log2 of linear boosts (PictureSharp keeps log2 values).
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>

#include "ultrahdr/gainmapmath.h"
#include "ultrahdr/gainmapmetadata.h"
#include "ultrahdr/icc.h"

using namespace ultrahdr;

static bool toFraction(const float* v, uhdr_gainmap_metadata_frac* to) {
  const float *max = v, *min = v + 3, *gamma = v + 6, *base = v + 9, *alt = v + 12;
  bool single = max[0] == max[1] && max[0] == max[2] && min[0] == min[1] && min[0] == min[2] &&
                gamma[0] == gamma[1] && gamma[0] == gamma[2] && base[0] == base[1] && base[0] == base[2] &&
                alt[0] == alt[1] && alt[0] == alt[2];
  for (int i = 0; i < (single ? 1 : 3); i++) {
    if (!floatToSignedFraction(max[i], &to->gainMapMaxN[i], &to->gainMapMaxD[i]) ||
        !floatToSignedFraction(min[i], &to->gainMapMinN[i], &to->gainMapMinD[i]) ||
        !floatToUnsignedFraction(gamma[i], &to->gainMapGammaN[i], &to->gainMapGammaD[i]) ||
        !floatToSignedFraction(base[i], &to->baseOffsetN[i], &to->baseOffsetD[i]) ||
        !floatToSignedFraction(alt[i], &to->alternateOffsetN[i], &to->alternateOffsetD[i]))
      return false;
  }
  if (single) {
    for (int i = 1; i < 3; i++) {
      to->gainMapMaxN[i] = to->gainMapMaxN[0];
      to->gainMapMaxD[i] = to->gainMapMaxD[0];
      to->gainMapMinN[i] = to->gainMapMinN[0];
      to->gainMapMinD[i] = to->gainMapMinD[0];
      to->gainMapGammaN[i] = to->gainMapGammaN[0];
      to->gainMapGammaD[i] = to->gainMapGammaD[0];
      to->baseOffsetN[i] = to->baseOffsetN[0];
      to->baseOffsetD[i] = to->baseOffsetD[0];
      to->alternateOffsetN[i] = to->alternateOffsetN[0];
      to->alternateOffsetD[i] = to->alternateOffsetD[0];
    }
  }
  return floatToUnsignedFraction(v[15], &to->baseHdrHeadroomN, &to->baseHdrHeadroomD) &&
         floatToUnsignedFraction(v[16], &to->alternateHdrHeadroomN, &to->alternateHdrHeadroomD);
}

int main(int argc, char** argv) {
  if (argc != 4) {
    std::fprintf(stderr, "usage: ultrahdr_iso_oracle fractions|iso input.bin output.bin\n");
    return 2;
  }
  FILE* in = std::fopen(argv[2], "rb");
  FILE* out = std::fopen(argv[3], "wb");
  if (!in || !out) return 1;
  if (std::strcmp(argv[1], "fractions") == 0) {
    float v;
    while (std::fread(&v, 4, 1, in) == 1) {
      uint32_t un = 0, ud = 0, sd = 0;
      int32_t sn = 0;
      uint8_t uok = floatToUnsignedFraction(v, &un, &ud), sok = floatToSignedFraction(v, &sn, &sd);
      std::fwrite(&uok, 1, 1, out);
      std::fwrite(&un, 4, 1, out);
      std::fwrite(&ud, 4, 1, out);
      std::fwrite(&sok, 1, 1, out);
      std::fwrite(&sn, 4, 1, out);
      std::fwrite(&sd, 4, 1, out);
    }
  } else if (std::strcmp(argv[1], "iso") == 0) {
    uint8_t useBase;
    float v[17];
    while (std::fread(&useBase, 1, 1, in) == 1) {
      if (std::fread(v, sizeof v, 1, in) != 1) return 1;
      uhdr_gainmap_metadata_frac frac{};
      frac.backwardDirection = false;
      frac.useBaseColorSpace = useBase != 0;
      int32_t length = -1;
      std::vector<uint8_t> payload;
      if (toFraction(v, &frac)) {
        if (uhdr_gainmap_metadata_frac::encodeGainmapMetadata(&frac, payload).error_code != UHDR_CODEC_OK) return 1;
        length = (int32_t)payload.size();
      }
      std::fwrite(&length, 4, 1, out);
      std::fwrite(payload.data(), 1, payload.size(), out);
    }
  } else if (std::strcmp(argv[1], "icc") == 0) {
    // The profiles a gain map is written with (sRGB transfer), BT.709 then Display P3, without the APP2 identifier.
    for (uhdr_color_gamut_t gamut : {UHDR_CG_BT_709, UHDR_CG_DISPLAY_P3}) {
      std::shared_ptr<DataStruct> icc = IccHelper::writeIccProfile(UHDR_CT_SRGB, gamut);
      int32_t length = (int32_t)(icc->getLength() - kICCIdentifierSize);
      std::fwrite(&length, 4, 1, out);
      std::fwrite((uint8_t*)icc->getData() + kICCIdentifierSize, 1, length, out);
    }
  } else {
    return 2;
  }
  std::fclose(in);
  std::fclose(out);
  return 0;
}
