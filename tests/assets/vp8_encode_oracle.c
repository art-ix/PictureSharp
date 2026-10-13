// libwebp 1.6.0 API oracle for options not exposed by cwebp.
#include <stdio.h>
#include <stdlib.h>
#include "src/webp/encode.h"
int main(int argc, char** argv) {
  if (argc != 7) return 1;
  int width = atoi(argv[3]), height = atoi(argv[4]);
  if (width <= 0 || height <= 0 || width > 16383 || height > 16383) return 2;
  const size_t size = (size_t)width * height * 4;
  uint8_t* rgba = malloc(size); FILE* input = fopen(argv[1], "rb");
  if (!rgba || !input || fread(rgba, 1, size, input) != size) return 3;
  fclose(input);
  WebPConfig config; WebPConfigInit(&config);
  config.method = atoi(argv[5]); config.partitions = atoi(argv[6]);
  if (!WebPValidateConfig(&config)) return 4;
  WebPPicture picture; WebPPictureInit(&picture);
  picture.width = width; picture.height = height; picture.use_argb = 1;
  if (!WebPPictureImportRGBA(&picture, rgba, 4 * width)) return 5;
  WebPMemoryWriter writer; WebPMemoryWriterInit(&writer);
  picture.writer = WebPMemoryWrite; picture.custom_ptr = &writer;
  if (!WebPEncode(&config, &picture)) return 6;
  FILE* output = fopen(argv[2], "wb");
  if (!output || fwrite(writer.mem, 1, writer.size, output) != writer.size) return 7;
  int result = fclose(output) != 0;
  WebPMemoryWriterClear(&writer); WebPPictureFree(&picture); free(rgba);
  return result;
}
