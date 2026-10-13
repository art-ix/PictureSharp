// Scalar libwebp 1.6.0 oracle. Used only by the test project.
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "src/dsp/dsp.h"
#include "src/dsp/cpu.h"
#include "src/enc/vp8i_enc.h"
#include "src/enc/cost_enc.h"
#include "src/utils/bit_writer_utils.h"
#include "src/utils/quant_levels_utils.h"
#include "src/webp/encode.h"

extern VP8CPUInfo VP8GetCPUInfo;
static unsigned state = 713;
static unsigned Next(void) { state = state * 1664525u + 1013904223u; return state; }
static void U32(FILE* f, unsigned v) { for (int i = 0; i < 4; ++i) fputc((v >> (8 * i)) & 255, f); }
static void U64(FILE* f, uint64_t v) { U32(f, (unsigned)v); U32(f, (unsigned)(v >> 32)); }
static void Shorts(FILE* f, const int16_t* v, int n) {
  for (int i = 0; i < n; ++i) { fputc(v[i] & 255, f); fputc((v[i] >> 8) & 255, f); }
}
static void Fill(uint8_t* p, size_t n) { for (size_t i = 0; i < n; ++i) p[i] = Next() >> 24; }
static void Primitives(FILE* f) {
  VP8GetCPUInfo = NULL;
  VP8EncDspInit();
  U32(f, 96);
  for (int test = 0; test < 96; ++test) {
    uint8_t source[512], pred[512], left[33], top[20], boundary[13], predictions[PRED_SIZE_ENC] = {0};
    int16_t coeffs[256], wht[16], levels[16], quant[16];
    Fill(source, sizeof(source)); Fill(pred, sizeof(pred));
    Fill(left, sizeof(left)); Fill(top, sizeof(top)); Fill(boundary, sizeof(boundary));
    if (test < 4) { memset(source, test & 1 ? 255 : 0, sizeof(source)); memset(pred, test & 2 ? 255 : 0, sizeof(pred)); }
    fwrite(source, 1, sizeof(source), f); fwrite(pred, 1, sizeof(pred), f);
    fwrite(left, 1, sizeof(left), f); fwrite(top, 1, sizeof(top), f); fwrite(boundary, 1, sizeof(boundary), f);
    for (int b = 0; b < 16; ++b) VP8FTransform(source + VP8DspScan[b], pred + VP8DspScan[b], coeffs + 16 * b);
    Shorts(f, coeffs, 256);
    VP8FTransformWHT(coeffs, wht); Shorts(f, wht, 16);
    for (int type = 0; type < 3; ++type) {
      VP8Matrix m;
      static const unsigned biases[3][2] = {{96,110},{96,108},{110,115}};
      static const unsigned sharpen[16] = {0,30,60,90,30,60,90,90,60,90,90,90,90,90,90,90};
      unsigned dc = 4 + Next() % 300, ac = 4 + Next() % 437;
      U32(f, dc); U32(f, ac);
      for (int i = 0; i < 16; ++i) {
        m.q[i] = i == 0 ? dc : ac; m.iq[i] = (1 << 17) / m.q[i];
        m.bias[i] = biases[type][i != 0] << 9;
        m.zthresh[i] = ((1 << 17) - 1 - m.bias[i]) / m.iq[i];
        m.sharpen[i] = type == 0 ? (sharpen[i] * m.q[i]) >> 11 : 0;
        quant[i] = coeffs[i];
      }
      U32(f, VP8EncQuantizeBlock(quant, levels, &m));
      Shorts(f, quant, 16); Shorts(f, levels, 16);
    }
    VP8EncPredLuma16(predictions, test & 1 ? left + 1 : NULL, test & 2 ? top : NULL);
    VP8EncPredChroma8(predictions, test & 1 ? left + 1 : NULL, test & 2 ? top : NULL);
    VP8EncPredLuma4(predictions, boundary + 5);
    fwrite(predictions, 1, sizeof(predictions), f);
    U32(f, VP8SSE16x16(source, pred));
    static const uint16_t weights[16] = {38,32,20,9,32,28,17,7,20,17,10,4,9,7,4,2};
    U32(f, VP8TDisto16x16(source, pred, weights));
    VP8Histogram hist;
    VP8CollectHistogram(source, pred, 0, 16, &hist);
    U32(f, hist.max_value); U32(f, hist.last_non_zero);
    VP8BitWriter bw; VP8BitWriterInit(&bw, 1);
    for (int i = 0; i < 512; ++i) VP8PutBit(&bw, source[i] & 1, pred[i]);
    VP8PutBits(&bw, 0xabcdef, 24); VP8PutSignedBits(&bw, -137, 8); VP8PutSignedBits(&bw, 0, 8);
    VP8BitWriterFinish(&bw); U32(f, (unsigned)VP8BitWriterSize(&bw));
    fwrite(VP8BitWriterBuf(&bw), 1, VP8BitWriterSize(&bw), f); VP8BitWriterWipeOut(&bw);
    for (int levels_count = 2; levels_count <= 256; levels_count *= 2) {
      uint8_t alpha[512]; memcpy(alpha, source, 512);
      if (!QuantizeLevels(alpha, 32, 16, levels_count, NULL)) exit(4);
      fwrite(alpha, 1, 512, f);
    }
  }
}
static void Plane(FILE* f, const uint8_t* p, int stride, int width, int height) {
  for (int y = 0; y < height; ++y) fwrite(p + y * stride, 1, width, f);
}
static void Yuv(FILE* f) {
  static const int sizes[][2] = {{1,1},{1,17},{17,1},{3,7},{8,8},{17,19},{64,65}};
  U32(f, 7 * 3 * 4);
  for (int size = 0; size < 7; ++size)
  for (int dither = 0; dither < 3; ++dither)
  for (int alpha = 0; alpha < 4; ++alpha) {
    const int w = sizes[size][0], h = sizes[size][1];
    uint8_t* rgba = malloc(w * h * 4); Fill(rgba, w * h * 4);
    for (int i = 0; i < w * h; ++i) {
      if (alpha == 0) rgba[4 * i + 3] = 255;
      if (alpha == 1) rgba[4 * i + 3] = 0;
      if (alpha == 2 && ((i / w) / 8 + (i % w) / 8) % 3 != 0) rgba[4 * i + 3] = 0;
    }
    U32(f, w); U32(f, h); U32(f, dither); fwrite(rgba, 1, w * h * 4, f);
    WebPPicture pic; WebPPictureInit(&pic); pic.width = w; pic.height = h; pic.use_argb = 1;
    if (!WebPPictureImportRGBA(&pic, rgba, 4 * w) || !WebPPictureARGBToYUVADithered(&pic, WEBP_YUV420, .5f * dither)) exit(5);
    Plane(f, pic.y, pic.y_stride, w, h); Plane(f, pic.u, pic.uv_stride, (w+1)/2, (h+1)/2); Plane(f, pic.v, pic.uv_stride, (w+1)/2, (h+1)/2);
    pic.use_argb = 0; WebPCleanupTransparentArea(&pic);
    Plane(f, pic.y, pic.y_stride, w, h); Plane(f, pic.u, pic.uv_stride, (w+1)/2, (h+1)/2); Plane(f, pic.v, pic.uv_stride, (w+1)/2, (h+1)/2);
    WebPPictureFree(&pic); free(rgba);
  }
}
static void Matrix(FILE* f, const VP8Matrix* m) {
  for (int i = 0; i < 16; ++i) { U32(f, m->q[i]); U32(f, m->iq[i]); U32(f, m->bias[i]); U32(f, m->zthresh[i]); U32(f, m->sharpen[i]); }
}
static void Analysis(FILE* f) {
  U32(f, 6 * 7 * 4);
  for (int preset = 0; preset < 6; ++preset)
  for (int method = 0; method < 7; ++method)
  for (int quality_idx = 0; quality_idx < 4; ++quality_idx) {
    static const int qualities[4] = {0,35,75,100};
    const int w = 65, h = 49, quality = qualities[quality_idx];
    WebPConfig config; WebPConfigPreset(&config, (WebPPreset)preset, quality);
    config.method = method;
    if (quality_idx == 1) config.segments = 1;
    WebPPicture pic; WebPPictureInit(&pic); pic.width = w; pic.height = h;
    if (!WebPPictureAlloc(&pic)) exit(6);
    for (int y = 0; y < h; ++y)
    for (int x = 0; x < w; ++x) pic.y[y * pic.y_stride + x] = x < 24 ? 128 : x < 48 ? (x + 2 * y) : (Next() >> 24);
    for (int y = 0; y < (h+1)/2; ++y)
    for (int x = 0; x < (w+1)/2; ++x) {
      pic.u[y * pic.uv_stride + x] = x < 24 ? (64 + 2 * x) : (Next() >> 24);
      pic.v[y * pic.uv_stride + x] = x < 24 ? 128 : (Next() >> 24);
    }
    U32(f, w); U32(f, h); U32(f, preset); U32(f, method); U32(f, quality); U32(f, config.segments);
    U32(f, config.sns_strength); U32(f, config.filter_strength); U32(f, config.filter_sharpness);
    Plane(f, pic.y, pic.y_stride, w, h); Plane(f, pic.u, pic.uv_stride, (w+1)/2, (h+1)/2); Plane(f, pic.v, pic.uv_stride, (w+1)/2, (h+1)/2);
    VP8Encoder enc; memset(&enc, 0, sizeof(enc));
    enc.config = &config; enc.pic = &pic; enc.method = method;
    enc.mb_w = (w + 15) / 16; enc.mb_h = (h + 15) / 16; enc.preds_w = 4 * enc.mb_w + 1;
    enc.num_parts = 1; enc.segment_hdr.num_segments = config.segments;
    const int mb_count = enc.mb_w * enc.mb_h;
    enc.mb_info = calloc(mb_count, sizeof(*enc.mb_info));
    uint8_t* preds = calloc(enc.preds_w * (4 * enc.mb_h + 1), 1);
    uint32_t* nz = calloc(enc.mb_w + 1, sizeof(*nz));
    enc.preds = preds + enc.preds_w + 1; enc.nz = nz + 1;
    enc.y_top = malloc(32 * enc.mb_w); enc.uv_top = enc.y_top + 16 * enc.mb_w;
    if (!VP8EncAnalyze(&enc)) exit(7);
    VP8SetSegmentParams(&enc, quality);
    U32(f, enc.alpha); U32(f, enc.uv_alpha); U32(f, enc.segment_hdr.num_segments);
    U32(f, enc.base_quant); U32(f, enc.dq_uv_dc); U32(f, enc.dq_uv_ac); U32(f, enc.filter_hdr.level);
    for (int i = 0; i < mb_count; ++i) {
      const VP8MBInfo* b = enc.mb_info + i;
      fputc(b->type, f); fputc(b->uv_mode, f); fputc(b->segment, f); fputc(b->alpha, f);
    }
    fwrite(preds, 1, enc.preds_w * (4 * enc.mb_h + 1), f);
    for (int i = 0; i < 4; ++i) {
      const VP8SegmentInfo* m = &enc.dqm[i];
      U32(f, m->alpha); U32(f, m->beta); U32(f, m->quant); U32(f, m->fstrength);
      if (i < enc.segment_hdr.num_segments) {
        U32(f, m->lambda_i4); U32(f, m->lambda_i16); U32(f, m->lambda_uv); U32(f, m->lambda_mode);
        U32(f, m->lambda_trellis_i4); U32(f, m->lambda_trellis_i16); U32(f, m->lambda_trellis_uv); U32(f, m->tlambda);
        U32(f, m->min_disto); U32(f, (unsigned)m->i4_penalty);
        Matrix(f, &m->y1); Matrix(f, &m->y2); Matrix(f, &m->uv);
      }
    }
    VP8DefaultProbas(&enc); VP8EncDspCostInit(); VP8CalculateLevelCosts(&enc.proba);
    enc.max_i4_header_bits = 65536;
    enc.mb_header_limit = (score_t)256 * 510 * 8 * 1024 / mb_count;
    enc.top_derr = quality <= 98 ? calloc(enc.mb_w, sizeof(*enc.top_derr)) : NULL;
    VP8EncIterator it; VP8IteratorInit(&enc, &it);
    const VP8RDLevel rd_opt = method >= 6 ? RD_OPT_TRELLIS_ALL : method >= 5 ? RD_OPT_TRELLIS : method >= 3 ? RD_OPT_BASIC : RD_OPT_NONE;
    do {
      VP8ModeScore rd; VP8IteratorImport(&it, NULL); VP8Decimate(&it, &rd, rd_opt);
      U32(f, rd.nz); U64(f, rd.D); U64(f, rd.SD); U64(f, rd.R); U64(f, rd.H); U64(f, rd.score);
      fputc(it.mb->type, f); fputc(it.mb->uv_mode, f); fputc(it.mb->skip, f);
      for (int y = 0; y < 4; ++y) fwrite(it.preds + y * enc.preds_w, 1, 4, f);
      if (it.mb->type == 1) Shorts(f, rd.y_dc_levels, 16);
      Shorts(f, rd.y_ac_levels[0], 256); Shorts(f, rd.uv_levels[0], 128);
      Plane(f, it.yuv_out, BPS, 16, 16); Plane(f, it.yuv_out + 16, BPS, 16, 8);
      const unsigned dc_nz = it.mb->type == 1 ? rd.nz & (1 << 24) : *it.nz & (1 << 24);
      *it.nz = rd.nz | dc_nz;
      if (it.mb->type == 1) it.left_nz[8] = (rd.nz >> 24) & 1;
      VP8IteratorSaveBoundary(&it);
    } while (VP8IteratorNext(&it));
    free(enc.top_derr);
    free(enc.y_top); free(preds); free(nz); free(enc.mb_info); WebPPictureFree(&pic);
  }
}
static void Entropy(FILE* f) {
  VP8Encoder enc; memset(&enc, 0, sizeof(enc));
  VP8EncDspCostInit();
  Fill((uint8_t*)enc.proba.coeffs, sizeof(enc.proba.coeffs));
  fwrite(enc.proba.coeffs, 1, sizeof(enc.proba.coeffs), f);
  enc.proba.dirty = 1; VP8CalculateLevelCosts(&enc.proba);
  proba_t direct[NUM_TYPES][NUM_BANDS][NUM_CTX][NUM_PROBAS] = {{{{0}}}};
  VP8TBuffer tokens; VP8TBufferInit(&tokens, 8192);
  const int count = 2048; U32(f, count);
  for (int test = 0; test < count; ++test) {
    const int type = test % 4, first = type == 0 ? 1 : 0, ctx = (test / 4) % 3;
    int16_t levels[16] = {0};
    const int last = test % 17 - 1;
    for (int i = first; i <= last; ++i) levels[i] = Next() & 1 ? (int)(Next() % 4095) - 2047 : 0;
    if (last >= first) levels[last] = (test & 1 ? -1 : 1) * (test % 2047 + 1);
    U32(f, type); U32(f, ctx); Shorts(f, levels, 16);
    VP8Residual res; VP8InitResidual(first, type, &enc, &res); VP8SetResidualCoeffs(levels, &res);
    U32(f, VP8GetResidualCost(ctx, &res));
    VP8RecordCoeffTokens(ctx, &res, &tokens);
    res.stats = direct[type]; VP8RecordCoeffs(ctx, &res);
  }
  for (int i = 0; i < 1056; ++i) U32(f, ((proba_t*)enc.proba.stats)[i]);
  for (int i = 0; i < 1056; ++i) U32(f, ((proba_t*)direct)[i]);
  VP8BitWriter bw; VP8BitWriterInit(&bw, 1);
  U32(f, (unsigned)VP8EstimateTokenSize(&tokens, (uint8_t*)enc.proba.coeffs));
  VP8EmitTokens(&tokens, &bw, (uint8_t*)enc.proba.coeffs, 1);
  VP8BitWriterFinish(&bw); U32(f, (unsigned)VP8BitWriterSize(&bw));
  fwrite(VP8BitWriterBuf(&bw), 1, VP8BitWriterSize(&bw), f); VP8BitWriterWipeOut(&bw);
}
static void Sharp(FILE* f) {
  static const int sizes[][2] = {{1,1},{3,7},{4,4},{8,8},{17,19},{64,65}};
  U32(f, 6 * 3);
  for (int size = 0; size < 6; ++size)
  for (int alpha = 0; alpha < 3; ++alpha) {
    const int w = sizes[size][0], h = sizes[size][1];
    uint8_t* rgba = malloc(w * h * 4); Fill(rgba, w * h * 4);
    for (int i = 0; i < w * h; ++i) if (alpha < 2) rgba[4 * i + 3] = alpha == 0 ? 255 : 0;
    U32(f, w); U32(f, h); fwrite(rgba, 1, w * h * 4, f);
    WebPPicture pic; WebPPictureInit(&pic); pic.width = w; pic.height = h; pic.use_argb = 1;
    if (!WebPPictureImportRGBA(&pic, rgba, 4 * w) || !WebPPictureSharpARGBToYUVA(&pic)) exit(8);
    Plane(f, pic.y, pic.y_stride, w, h); Plane(f, pic.u, pic.uv_stride, (w+1)/2, (h+1)/2); Plane(f, pic.v, pic.uv_stride, (w+1)/2, (h+1)/2);
    WebPPictureFree(&pic); free(rgba);
  }
}
int main(int argc, char** argv) {
  if (argc != 2) return 1;
  FILE* f = fopen(argv[1], "wb"); if (!f) return 2;
  Primitives(f); Yuv(f); Analysis(f); Entropy(f); Sharp(f); return fclose(f) != 0;
}
