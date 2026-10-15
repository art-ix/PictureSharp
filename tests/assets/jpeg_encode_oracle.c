/* Test-only adapter to the system libjpeg-turbo. Not part of PictureSharp. */
#include <stdio.h>
#include <stdlib.h>
#include <jpeglib.h>

int main(int argc, char **argv) {
  if (argc != 11) return 2;
  int width = atoi(argv[3]), height = atoi(argv[4]);
  int gray = atoi(argv[10]);
  FILE *input = fopen(argv[1], "rb"), *output = fopen(argv[2], "wb");
  if (!input || !output) return 3;
  unsigned char *row = malloc((size_t)width * (gray ? 1 : 3));
  if (!row) return 4;
  struct jpeg_compress_struct c;
  struct jpeg_error_mgr error;
  c.err = jpeg_std_error(&error);
  jpeg_create_compress(&c);
  jpeg_stdio_dest(&c, output);
  c.image_width = width; c.image_height = height;
  c.input_components = gray ? 1 : 3;
  c.in_color_space = gray ? JCS_GRAYSCALE : JCS_RGB;
  jpeg_set_defaults(&c);
  jpeg_set_quality(&c, atoi(argv[5]), TRUE);
  int sampling = atoi(argv[6]);
  c.comp_info[0].h_samp_factor = (sampling == 1 || sampling == 2) ? 2 : 1;
  c.comp_info[0].v_samp_factor = (sampling == 2 || sampling == 3) ? 2 : 1;
  c.optimize_coding = atoi(argv[7]);
  c.restart_interval = atoi(argv[9]);
  c.density_unit = 1; c.X_density = c.Y_density = 96;
  c.dct_method = JDCT_ISLOW;
  if (atoi(argv[8])) jpeg_simple_progression(&c);
  jpeg_start_compress(&c, TRUE);
  while (c.next_scanline < c.image_height) {
    if (fread(row, gray ? 1 : 3, width, input) != (size_t)width) return 5;
    JSAMPROW rows[1] = { row };
    jpeg_write_scanlines(&c, rows, 1);
  }
  jpeg_finish_compress(&c);
  jpeg_destroy_compress(&c);
  fclose(input); fclose(output); free(row);
  return 0;
}
