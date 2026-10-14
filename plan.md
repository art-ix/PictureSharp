# RasterSharp: plan implementacji

## Kontekst

Potrzebna jest biblioteka .NET (**net10.0+**) do kodowania i dekodowania WebP, napisana w 100% w kodzie zarządzanym (C#), **bez zależności NuGet i bez natywnych bibliotek**. Używamy tylko BCL, w tym `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`), które jest częścią shared framework i działa na każdej platformie. Biblioteka ma też czytać PNG, BMP i JPEG (do przekodowywania) oraz zapisywać PNG, BMP i JPEG. Centralnym typem jest klasa `Image` z API w stylu ImageSharp (`Load`/`Mutate`/`Clone`/`SaveAs*`, od fazy 9.5; wcześniej wzorowana na `System.Drawing.Image`/`Bitmap`).

Ustalenia z użytkownikiem:
- **WebP w pełnym zakresie** (odczyt i zapis): lossy VP8, lossless VP8L, alfa ALPH, animacja ANIM/ANMF, metadane ICCP/EXIF/XMP.
- **PNG:** odczyt i zapis. **BMP:** odczyt i zapis. **JPEG:** odczyt i zapis (enkoder w fazie 5.5).
- **GIF:** odczyt i zapis (w tym animacje) oraz konwersja animowany GIF ↔ animowany WebP (faza 9.9).
- **Deflate/zlib piszemy sami.** Nie używamy `ZLibStream`, bo pod spodem woła natywne zlib.
- **`Image` obejmuje:** skalowanie, geometrię (crop/rotate/flip/EXIF), dostęp do pikseli, operacje na kolorach i filtry (od fazy 9.5).

Stan środowiska (aktualizacja w fazie 6):
- Lokalny katalog roboczy pozostaje `/home/artur/Projekty/ai/webpsharp`; projekt i repozytorium GitHub noszą nazwę `RasterSharp`. Target to .NET 10.
- Nowa referencja to **libwebp 1.6.0**, tag `v1.6.0`, commit `4fa21912338357f89e4fd51cf2368325b59e9bd9`.
- `scripts/build-webp-reference.sh` pobiera źródła do `reference/libwebp-1.6.0/` i buduje narzędzia wyroczni w `reference/libwebp-1.6.0-build/`. Testy i skrypty korpusu wybierają je automatycznie; można ustawić `RASTERSHARP_WEBP_TOOLS`.
- Systemowe narzędzia 1.3.2 i stara kopia `reference/libwebp/` pozostają do porównań. ImageMagick i Pillow służą jako pozostałe wyrocznie.

**Kluczowa decyzja:** od fazy 6 nowe porty bazują na **libwebp v1.6.0**. Enkoder lossless używa kosztów stałoprzecinkowych tej wersji (23 bity ułamkowe). Dekodery faz 1–3 pochodzą z 1.3.2; ponowne obliczenie 248 wpisów `reference.json` z `dwebp` 1.6.0 nie zmieniło wyników. Historycznego korpusu nie regenerujemy przy samej zmianie wyroczni.
- Dekoder sprawdzamy piksel w piksel z `dwebp`.
- Dla enkodera celujemy w wynik **bajt w bajt identyczny z `cwebp`** przy tych samych parametrach. Kryterium zastępczym jest parytet PSNR i rozmiaru pliku określony w DoD danej fazy.

---

## Struktura rozwiązania

```
RasterSharp/
  RasterSharp.slnx
  Directory.Build.props          # net10.0, Nullable, LangVersion 14.0, TreatWarningsAsErrors,
                                 # IsAotCompatible, IsTrimmable, GenerateDocumentationFile
  THIRD-PARTY-NOTICES.txt        # licencja BSD-3 libwebp (kod portowany = praca pochodna) + inne źródła
  .gitignore                     # reference/, tests/assets/external/, bin/obj
  reference/libwebp-1.6.0/       # źródła tagu v1.6.0 (tylko do czytania, poza buildem)
  src/RasterSharp/                 # biblioteka, ZERO PackageReference
    Image.cs, ImageFrame.cs, ImageFrameCollection.cs, ImageInfo.cs, ImageFormat.cs,
    Rgba32.cs, PixelFormat.cs, ImageMetadata.cs, BitmapData.cs, enums (RotateFlipType,
    ResizeMode, ResamplingFilter, ImageLockMode, ExifOrientation), DecoderOptions.cs,
    EncoderOptions.cs, WebP.cs (fasada),
    ImageFormatException.cs
    Formats/
      IImageFormatDetector.cs, IImageDecoder.cs, IImageEncoder.cs, CodecRegistry.cs, ImageCodec.cs (internal)
      # Każdy format zawiera też publiczne opcje enkodera, enumy i rozszerzenia Image (namespace RasterSharp).
      WebP/ Container/ (RiffReader, RiffWriter, chunki VP8X/ALPH/ANIM/ANMF/ICCP/EXIF/XMP)
            Lossless/ (Vp8LBitReader/Writer, Huffman*, ColorCache, Transforms, Vp8LDecoder,
                       Vp8LEncoder, BackwardRefs*, Histogram*, Predictor*, NearLossless)
            Lossy/    (Vp8BitReader/Writer, Vp8Tables, Vp8Decoder, Vp8Dsp*, Vp8Encoder,
                       Analysis, Quant, Filter, Tokens, Cost, Iterator, Syntax)
            Alpha/    (AlphaDecoder, AlphaEncoder, AlphaFilters, QuantLevels)
            Animation/(AnimDecoder: kompozycja klatek, AnimEncoder: sub-rect + blend/dispose)
            Color/    (YuvToRgb + fancy upsampling, RgbToYuv, SharpYuv)
            WebPDecoder.cs, WebPEncoder.cs
      Png/  (PngDecoder, PngEncoder, PngFilters, Adam7, Apng)
      Bmp/  (BmpDecoder, BmpEncoder, BmpHeaders, Rle)
      Jpeg/ (JpegDecoder, JpegHuffman, JpegIdct (islow), Progressive, Upsampler, ColorConverter,
            JpegEncoder, JpegFdct, JpegQuantizer, JpegHuffmanEncoder, JpegMarkerWriter)
      Gif/  (GifFile, GifDecoder, GifEncoder, GifLzwDecoder/Encoder, GifPaletteBuilder)
      Metadata/ (ExifReader/Patcher: Orientation, rozdzielczość; IccProfile)
    Compression/ (Inflater, Deflater, Adler32, Crc32)
    Processing/  (ImageProcessingContext.*, ResizeOptions, enumy przetwarzania,
                  Resizer, Kernels, Geometry, ColorOps, Compositor, PixelConverter,
                  Quantization/: MedianCutQuantizer, OctreeQuantizer, PaletteMapper, QuantHashTable)
    Internal/    (BitHelpers, pooling, SIMD helpers)
  tests/RasterSharp.Tests/         # xUnit (zależności tylko w testach)
    assets/ generate_corpus.py, make_reference.py, fetch-external.sh, reference.json, corpus/, reference/
  benchmarks/RasterSharp.Benchmarks/ # BenchmarkDotNet
  samples/RasterSharp.Cli/         # `rastersharp convert in.jpg out.webp -q 80`, `info`, `resize`
```

Zasady kodu:
- Tylko jedna publiczna przestrzeń nazw `RasterSharp`. Wszystkie kodeki są `internal`.
- Tabele z C przenosimy jako `static ReadOnlySpan<T> X => [...]`, co daje dane bez alokacji.
- Zamiast `unsafe` używamy `Span`, `ref` i `Unsafe.Add`/`MemoryMarshal`.
- Bufory tymczasowe bierzemy z `ArrayPool<T>.Shared`. Piksele obrazu leżą w `GC.AllocateUninitializedArray<Rgba32>`.

---

## Publiczne API

### Typy podstawowe
- `Rgba32`: struktura `{byte R,G,B,A}` w układzie sekwencyjnym, z konwersjami do i z `System.Drawing.Color` oraz `uint`. Przechowuje **alfę prostą** (niepremultiplikowaną), tak samo jak WebP i PNG.
- `PixelFormat` (do eksportu i importu): `Rgba32, Bgra32, Argb32, Rgb24, Bgr24, Gray8, Rgba32Premultiplied, Bgra32Premultiplied`.
- `ImageFormat`: klasa z instancjami statycznymi `WebP`, `Png`, `Bmp`, `Jpeg`, `Gif` (od fazy 9.9), `MemoryBmp`. Właściwości: `Name`, `MimeType`, `FileExtensions`, `CanDecode`, `CanEncode`.
- `ImageInfo` (wynik `Identify`, bez dekodowania pikseli): `Format`, `Width`, `Height`, `HasAlpha`, `FrameCount`, `IsAnimated`, `WebPCompression` (Lossy/Lossless/Mixed), `Orientation`, `HorizontalResolution`/`VerticalResolution`.
- `ImageMetadata`: `byte[]? IccProfile`, `Exif`, `Xmp`, `ExifOrientation Orientation`, `Clear()`.
- `ImageFormatException : FormatException`, rzucany dla uszkodzonych lub nierozpoznanych danych. Poprawne pliki z nieobsługiwanymi funkcjami (np. arytmetyczny JPEG) dają `NotSupportedException`.

### `Image` (sealed, `IDisposable`) — API od fazy 9.5

```csharp
// Wczytywanie (format z zawartości)
static Image Load(string path | Stream | ReadOnlySpan<byte> | ReadOnlyMemory<byte>, DecoderOptions? o = null); // byte[] bez kopii
static Task<Image> LoadAsync(string path | Stream [, DecoderOptions? o], CancellationToken ct = default);
static Image LoadPixelData(ReadOnlySpan<byte> px, int w, int h, PixelFormat fmt, int stride = 0);
static Image LoadPixelData(ReadOnlySpan<Rgba32> px, int w, int h);
static ImageInfo Identify(path | Stream | ReadOnlySpan<byte>);  static Task<ImageInfo> IdentifyAsync(path | Stream, ct);
static ImageFormat? DetectFormat(ReadOnlySpan<byte> | string path | Stream);
static Image CreateAnimation(TimeSpan frameDuration, params ReadOnlySpan<Image> frames);  // + IEnumerable<Image>
Image(int w, int h);  Image(int w, int h, Rgba32 background);

// Właściwości
int Width, Height;  Size Size;  Rectangle Bounds;  ImageFormat? SourceFormat;  bool HasAlpha;
ImageMetadata Metadata;   // DPI (HorizontalResolution/VerticalResolution), ICC, EXIF, XMP, Orientation
ImageFrameCollection Frames;  int FrameCount;  bool IsAnimated;  int LoopCount;  Rgba32 AnimationBackgroundColor;

// Piksele (pierwsza klatka, Frames.RootFrame)
Rgba32 this[int x, int y] { get; set; }   // Color konwertuje się niejawnie
Span<Rgba32> GetRowSpan(int y);  Span<Rgba32> GetPixelSpan();  Memory<Rgba32> GetPixelMemory();
void CopyPixelDataTo(Span<Rgba32>);  void CopyPixelDataTo(Span<byte>, PixelFormat, int stride = 0);
void CopyPixelDataFrom(ReadOnlySpan<byte>, PixelFormat, int stride = 0);  byte[] ToPixelData(PixelFormat = Rgba32);
bool IsOpaque();

// Przetwarzanie: Mutate zmienia obraz, Clone zwraca nowy (kopiuje piksele leniwie)
void Mutate(Action<ImageProcessingContext> op);  Image Clone(Action<ImageProcessingContext> op);  Image Clone();

// Zapis
void Save(string path);  void Save(string path, EncoderOptions o);  void Save(Stream s, ImageFormat f);  void Save(Stream s, EncoderOptions o);
Task SaveAsync(...);   // te same warianty + CancellationToken
// Per format (bloki extension(Image) w C# 14): SaveAsWebP/SaveAsPng/SaveAsJpeg/SaveAsBmp(path | Stream, options? = null),
// SaveAsXAsync(...), ToWebP/ToPng/ToJpeg/ToBmp(options? = null) -> byte[]
```

- **`ImageProcessingContext`** (`readonly ref struct`, nie może wyjść poza lambdę; `Size`, `FrameCount`). Operacje działają na wszystkich klatkach i zwracają kontekst:
  - `Resize(w, h)` (0 = proporcje), `Resize(Size)`, `Resize(w, h, ResamplingFilter)`, `Resize(w, h, ResizeMode, filter)`, `Resize(ResizeOptions)`, `Thumbnail(maxW, maxH)`;
  - `ResizeOptions` (rekord): `Size`, `Mode` (Stretch/Max/Crop/Pad/Min), `Filter` (Nearest/Bilinear/Bicubic/Lanczos3/Box/Hamming), `Position` (`AnchorPosition`), `PadColor`, `AllowUpscale`;
  - `Crop(Rectangle | x, y, w, h | w, h, anchor)`, `Pad(w, h, color, anchor)`, `Rotate(RotateMode)`, `Flip(FlipMode)`, `RotateFlip`, `AutoOrient`, `Rotate(float degrees, filter, background, expand)`;
  - `Grayscale`, `Invert`, `Sepia`, `Brightness`, `Contrast`, `Saturate`, `Fill(color[, rect])`, `MakeTransparent`, `BackgroundColor`, `Opacity`;
  - `BoxBlur`, `GaussianBlur`, `UnsharpMask`, `Sharpen`, `DrawImage(source, Point | AnchorPosition, opacity)`, `Apply(Action<ImageFrame>)`.
- **`ImageFrame`:** `Width`, `Height`, `Duration` (TimeSpan), indeksator `Rgba32`, `GetRowSpan`, `GetPixelSpan`, `GetPixelBytes`, `Clone()`. Każda klatka to pełne płótno, a dekoder zwraca klatki już skomponowane, jak `WebPAnimDecoder`.
- **`ImageFrameCollection`:** `IReadOnlyList<ImageFrame>` plus `RootFrame`, `Add(ImageFrame)`, `Add(Image, TimeSpan)`, `Insert`, `RemoveAt`, `Move`, `CloneFrame(i)`, `ExportFrame(i)`. Wymiary klatek muszą być równe wymiarom obrazu.
- **`Rgba32`:** `Parse`/`TryParse` (`ISpanParsable`: `#RGB`, `#RGBA`, `#RRGGBB`, `#RRGGBBAA`, nazwy kolorów), `ToHex`, `Deconstruct`, niejawne konwersje z/do `Color`.
- **`AutoOrient()`** stosuje orientację EXIF 1–8 i ustawia tag na 1 bezpośrednio w bajtach EXIF.
- Publiczne API jest zapisane w `tests/assets/PublicApi.approved.txt` i pilnowane przez `PublicApiTests`.

### Opcje
Opcje są rekordami (`with`), a wartości sprawdzamy przy ustawianiu (`init` ze słowem `field`), więc błędna wartość rzuca `ArgumentOutOfRangeException` od razu.
- **`DecoderOptions`:**
  - `MaxPixels` (domyślnie 2^28)
  - `FirstFrameOnly`
  - `ApplyExifOrientation` (domyślnie false)
  - `IgnoreMetadata`
  - `WebPFancyUpsampling` (true)
  - `WebPBypassFiltering`
  - `StrictChecksums` (PNG CRC)
- **`WebPEncoderOptions`** (odpowiednik `WebPConfig`):
  - `Quality` (75), `Lossless`, `Method` (0–6, domyślnie 4), `Preset` (Default/Picture/Photo/Drawing/Icon/Text)
  - `AlphaQuality`, `AlphaCompression`, `AlphaFilter`, `Exact`, `NearLossless`
  - `SnsStrength`, `FilterStrength`, `FilterSharpness`, `AutoFilter`, `Segments`, `Partitions`
  - `TargetSize`, `TargetPsnr`, `Passes`, `UseSharpYuv`
  - `Metadata` (flagi Icc|Exif|Xmp, domyślnie All)
  - Animacja: `AllowMixed`, `MinimizeSize`, `KeyframeMin`/`KeyframeMax`
  - `MaxDegreeOfParallelism`
- **`PngEncoderOptions`:**
  - `CompressionLevel` (0–9, domyślnie 6)
  - `ColorType` (Auto: wybiera Palette, Gray, Rgb lub Rgba w zależności od danych)
  - `FilterStrategy` (Adaptive)
  - `Interlace`, `Metadata`, `SaveAnimation` (APNG)
- **`BmpEncoderOptions`:**
  - `BitsPerPixel` (Auto: 1/4/8 z paletą przy ≤256 kolorach, 24 dla obrazów nieprzezroczystych, 32 z kanałem alfa)
  - `Metadata` (profil ICC w nagłówku BITMAPV5HEADER)
- **`JpegEncoderOptions`:** `Quality` (1–100), `Subsampling`, `Progressive`, `OptimizeCoding`, `RestartInterval`, `BackgroundColor` (`Rgba32`, nieprzezroczysty), `Metadata`.
- **`GifEncoderOptions`** (faza 9.9):
  - `ColorCount` (2–256, domyślnie 256), `Quantizer` (Auto/MedianCut/Octree), `Dither` (None/FloydSteinberg)
  - `TransparencyThreshold` (0–255, domyślnie 128), `Interlace`, `SaveAnimation`, `OptimizeFrames`, `Metadata` (XMP/ICC)

### Fasada `WebP` (odwzorowuje proste API z `decode.h`/`encode.h`)

```csharp
static bool TryGetInfo(ReadOnlySpan<byte> data, out ImageInfo info);
static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height);   // WebPDecodeRGBA
static byte[] DecodeBgra(...);  static Size DecodeInto(ReadOnlySpan<byte> data, Span<byte> dst, int stride, PixelFormat f);
static byte[] EncodeLosslessRgba(ReadOnlySpan<byte> rgba, int w, int h, int stride = 0);
static byte[] Encode(Image image, WebPEncoderOptions? o = null);
static void Convert(string inPath, string outPath, WebPEncoderOptions? o = null);   // jpg/png/bmp/gif/webp -> webp
```

Przykład użycia:

```csharp
using var img = Image.Load("photo.jpg");
img.Mutate(x => x.AutoOrient().Thumbnail(1920, 1080));
img.SaveAsWebP("photo.webp", new() { Quality = 82 });
```

### Wyraźne ograniczenia (do opisania w dokumentacji)
- Brak zarządzania kolorem: ICC jest przenoszony, ale nie stosowany. CMYK→RGB konwertujemy naiwnie.
- PNG 16-bit jest konwertowane do 8 bitów.
- BMP zapisujemy zawsze bez kompresji (RLE tylko przy odczycie). Animacje zapisujemy jako pierwszą klatkę (także JPEG).
- GIF: alfa binarna (próg `TransparencyThreshold`), maksymalnie 256 kolorów na klatkę (kwantyzacja), czasy klatek w krokach 10 ms, EXIF nie jest zapisywany.
- Strumień wejściowy jest wczytywany w całości do pamięci.

---

## Fazy implementacji

Kolejność wynika z zależności:
- dekoder VP8L jest potrzebny do kanału alfa,
- dekodery służą do weryfikacji enkoderów,
- enkoder VP8L jest potrzebny do kodowania alfy w lossy.

Każda faza kończy się zielonymi testami.

### Faza 0: szkielet (ok. 1,5k LOC)
- Zainicjować git (po zgodzie użytkownika).
- Utworzyć solution, `Directory.Build.props`, projekty src/tests/benchmarks/samples.
- `git clone --branch v1.3.2 https://chromium.googlesource.com/webm/libwebp reference/libwebp`.
- Typy podstawowe: `Rgba32`, `PixelFormat`, `ImageFormat`, `ImageFrame`, `Image` (konstrukcja, piksele, `Clone`, `LockBits`, `PixelConverter`), wyjątki, `FormatDetector` (sygnatury RIFF/WEBP, PNG, JPEG SOI), `CodecRegistry`, stub `Save`/`From*`.
- `tests/assets/generate_corpus.py` tworzy korpus testowy narzędziami `cwebp` (różne `-q`, `-m 0..6`, `-lossless`, `-alpha_filter`, `-exact`, `-segments`, `-sharp_yuv`), `img2webp`/`webpmux` (animacje, metadane) i `convert` (PNG dla wszystkich typów kolorów i głębi, Adam7, palety, tRNS; JPEG baseline/progressive, 4:4:4/4:2:2/4:2:0/4:1:1, gray, CMYK, DRI, EXIF orientation 1–8).
- `make_reference.py` (uruchamiany przez `python3 -I`) zapisuje do `reference.json` SHA-256 pikseli RGBA zdekodowanych przez `dwebp -pam` oraz Pillow (PNG/JPEG/animacje WebP przez WebPAnimDecoder). Testy nie wymagają wtedy narzędzi w runtime.
- `fetch-external.sh` (pobieranie za zgodą użytkownika) ściąga do `tests/assets/external/`:
  - libwebp-test-data (wektory zgodności VP8),
  - PngSuite,
  - BMP Suite (Jason Summers, katalogi g/q/b: poprawne, wątpliwe, błędne),
  - zestaw Kodak (do porównań enkodera).

### Faza 1: kontener WebP i dekoder lossless VP8L (ok. 2k LOC)
Źródła: `src/dec/webp_dec.c`, `vp8l_dec.c`, `src/utils/bit_reader*`, `huffman_utils.c`, `color_cache_utils.c`, `src/dsp/lossless.c`, `src/demux/demux.c`. Specyfikacja: RFC 9649.
- Parser RIFF w trzech formach: prosta VP8, prosta VP8L, rozszerzona VP8X. Walidacja rozmiarów i kolejności chunków.
- `Vp8LBitReader` z 64-bitowym prefetchem.
- Dwupoziomowe tablice Huffmana.
- Meta-kody (entropy image), color cache, mapowanie odległości `kCodeToPlane`.
- Odwrotne transformacje: predictor (14 trybów), cross-color, subtract-green, color-indexing z pakowaniem pikseli.
- `Image.FromFile` działa dla lossless WebP. `Identify` działa dla wszystkich wariantów.
- **DoD:** wszystkie pliki lossless z korpusu mają identyczny hash z `dwebp`.

### Faza 2: dekoder lossy VP8, alfa, animacja, metadane (ok. 3,5k LOC)
Źródła: `src/dec/vp8_dec.c`, `tree_dec.c`, `quant_dec.c`, `frame_dec.c`, `io_dec.c`, `alpha_dec.c`, `src/dsp/dec.c`, `yuv.h`, `upsampling.c`, `filters.c`, `src/demux/anim_decode.c`. Specyfikacja: RFC 6386.
- Nagłówek ramki i partycje, dekoder boolowski.
- Segmenty, nagłówek filtra, tablice kwantyzacji DC/AC, aktualizacje prawdopodobieństw.
- Tryby intra (I16, I4, chroma), dekodowanie tokenów z kontekstami.
- Odwrotne WHT i DCT, predykcja, rekonstrukcja.
- Loop filter (simple i normal, delty dla segmentów i trybów, hev).
- YUV420→RGB z **fancy upsampling bit-exact** z libwebp.
- ALPH: tryby raw i lossless (przez VP8L), filtry none/horizontal/vertical/gradient.
- Animacja: kompozycja z ANIM/ANMF (blend, dispose, offsety ×2, czas trwania klatki, loop, kolor tła). Wynikiem są pełne klatki płótna, jak w `anim_decode.c`.
- Metadane ICCP/EXIF/XMP trafiają do `ImageMetadata`.
- Fasada `WebP.Decode*`.
- **DoD:**
  - hashe identyczne z `dwebp -pam` dla korpusu lossy+alfa oraz dla wektorów libwebp-test-data,
  - klatki animacji identyczne z Pillow.

### Faza 3: zlib i PNG, odczyt i zapis (ok. 2,5k LOC)
Specyfikacje: RFC 1950/1951, W3C PNG 3rd ed. Algorytmy referencyjne: zlib `puff.c` i `deflate.c`.
- `Inflater`: bloki stored/fixed/dynamic, szybkie tablice dekodowania.
- `Deflater`: hash chains z lazy matching, poziomy 0–9, dynamiczny Huffman z ograniczeniem długości kodu do 15.
- `Adler32`, `Crc32` (slicing-by-8).
- Dekoder PNG:
  - wszystkie typy kolorów i głębie 1/2/4/8/16, PLTE/tRNS, Adam7, wiele IDAT;
  - pHYs przekładane na DPI, iCCP (dekompresja), eXIf;
  - APNG (acTL/fcTL/fdAT) z kompozycją klatek.
- Enkoder PNG:
  - automatyczny dobór typu koloru, w tym paleta przy ≤256 kolorach,
  - adaptacyjny filtr wierszy (heurystyka min-sum-abs),
  - pHYs/iCCP/eXIf,
  - APNG dla obrazów animowanych.
- **DoD:**
  - PngSuite zgodne z Pillow,
  - deflate→inflate daje identyczne dane, a nasz deflate jest poprawnie rozpakowywany przez `ZLibStream` z BCL (tylko w testach),
  - PNG zapisane przez nas otwiera Pillow.

### Faza 4: BMP, odczyt i zapis (ok. 1k LOC)
Specyfikacje: Microsoft BITMAPFILEHEADER oraz nagłówki BITMAPCOREHEADER (OS/2, 12 B), BITMAPINFOHEADER (40 B), BITMAPV2/V3INFOHEADER (52/56 B), BITMAPV4HEADER (108 B), BITMAPV5HEADER (124 B), OS/2 2.x (64 B). Referencja testowa: Pillow i BMP Suite.
- Wykrywanie formatu po sygnaturze `BM`. `ImageFormat.Bmp` z rozszerzeniami `.bmp` i `.dib`.
- Dekoder BMP:
  - głębie 1/2/4/8 bit z paletą (BGR lub BGRX), 16 bit (domyślnie RGB555, BI_BITFIELDS np. RGB565), 24 bit, 32 bit (BGRX lub maski bitowe);
  - kompresje BI_RGB, BI_RLE8, BI_RLE4 (z przeskokami delta i końcem linii), BI_BITFIELDS, BI_ALPHABITFIELDS;
  - maski bitowe o dowolnej szerokości skalowane do 8 bitów;
  - kanał alfa z maski alfa (V3+ / BI_ALPHABITFIELDS); 32-bit BI_RGB traktujemy jak BGRX (nieprzezroczysty), tak jak Pillow;
  - obrazy bottom-up i top-down (ujemna wysokość), wyrównanie wierszy do 4 bajtów, offset danych z nagłówka pliku;
  - rozdzielczość z `XPelsPerMeter`/`YPelsPerMeter` przeliczana na DPI;
  - profil ICC osadzony w BITMAPV5HEADER (PROFILE_EMBEDDED);
  - BI_PNG przekazujemy do naszego dekodera PNG, a BI_JPEG do dekodera JPEG z kolejnej fazy (do tego czasu `NotSupportedException`);
  - walidacja rozmiarów, palet i strumieni RLE: uszkodzone pliki dają `ImageFormatException`.
- Enkoder BMP:
  - automatyczny dobór głębi: 1/4/8 bit z paletą przy ≤256 kolorach bez przezroczystości, 24 bit dla obrazów nieprzezroczystych, 32 bit BGRA z BITMAPV5HEADER i maską alfa, gdy obraz ma przezroczystość;
  - zapis DPI i (opcjonalnie) profilu ICC w BITMAPV5HEADER;
  - zapis bottom-up z wyrównaniem wierszy, bez kompresji.
- `Image.Save` rozpoznaje `.bmp`, dochodzą `SaveAsBmp`/`ToBmp` i `BmpEncoderOptions`.
- Korpus: warianty z ImageMagick (`BMP2:`, `BMP3:`, `BMP:`, głębie 1/4/8/16/24/32, RLE8, top-down) i Pillow; referencja pikseli z Pillow.
- **DoD:**
  - korpus BMP zgodny z Pillow piksel w piksel,
  - BMP Suite: pliki z katalogu `g` zgodne z Pillow (z ImageMagick tam, gdzie Pillow ich nie czyta albo ma znany błąd); pliki z `q` i `b` dekodowane tolerancyjnie albo odrzucane wyłącznie wyjątkiem `ImageFormatException`/`NotSupportedException`,
  - round-trip encode→decode bez strat dla każdej głębi,
  - nasze pliki BMP otwiera Pillow i ImageMagick.

### Faza 5: dekoder JPEG (ok. 2,5k LOC)
Specyfikacje: ITU-T T.81, JFIF, Adobe APP14. Algorytmy referencyjne: libjpeg-turbo `jidctint.c`, fancy upsampling i `jdcolor.c` (licencja IJG/BSD trafia do NOTICES).
- Markery SOF0/1/2, DHT, DQT (8- i 16-bit), DRI/RSTn, SOS.
- Skany przeplatane i nieprzeplatane, dowolne współczynniki próbkowania.
- Progressive: DC first/refine, AC first/refine, EOB runs.
- IDCT islow, upsampling h2v1/h2v2 fancy z fallbackiem przez replikację.
- Konwersja YCbCr/Gray/RGB/CMYK/YCCK→RGB.
- APP0 (DPI), APP1 (EXIF i XMP), APP2 (ICC składane z wielu kawałków).
- Tryb tolerancyjny dla obciętych plików.
- Nieobsługiwane (rzucają `NotSupportedException`): kodowanie arytmetyczne, SOF3 lossless, 12-bit.
- **DoD:** różnica względem Pillow (libjpeg-turbo, islow) ≤1 na kanał, docelowo 0.
- Weryfikacja:
  - korpus JPEG (ImageMagick, Pillow i pliki generowane własnym koderem w `generate_corpus.py`: YCCK, Adobe RGB, nietypowe próbkowanie, skany nieprzeplatane, brak DHT, 16-bitowe DQT) zgodny z Pillow piksel w piksel;
  - uszkodzone pliki (obcięte skany, błędne markery RST) zgodne z Pillow (`Category=Oracle`);
  - lokalne zdjęcia z `samples/` porównywane z Pillow (`Category=Local`).

### Faza 5.5: enkoder JPEG (ok. 2k LOC)
**Status: ukończona (2026-10-13).** Enkoder i publiczne API są zaimplementowane, bez nowych zależności biblioteki.
Weryfikacja: `dotnet build -c Release` (0 ostrzeżeń/błędów), `dotnet test -c Release --no-build` (1925/1925).
- 468 wariantów z korpusu: pełne pliki JPEG oraz zdekodowane piksele identyczne z Pillow 10.2.0 / libjpeg-turbo 2.1.5; jakości 1, 25, 50, 75, 90 i 100, obrazy kolorowe/szare/z alfą, baseline, optimize i progressive. Identyczność plików i pikseli zapewnia także równe rozmiary i PSNR dla q75/q90.
- 290 dodatkowych wariantów `Category=Oracle`: zgodność bajtowa z systemowym libjpeg, w tym 4:4:0, restarty, niepełne MCU i długie EOB runs; pliki otwierane przez Pillow, ImageMagick i nasz dekoder.
- Testy API, aktywnej klatki, kompozycji alfy, metadanych wielosegmentowych, DPI i walidacji opcji. Referencje enkodera: `tests/assets/jpeg_encoder_reference.json`; regeneracja: `python3 -I tests/assets/make_jpeg_encoder_reference.py`.

Specyfikacje: ITU-T T.81 (w tym Annex K: tablice kwantyzacji i Huffmana), JFIF 1.02, Exif (APP1), ICC (APP2). Algorytmy referencyjne: libjpeg-turbo `jcparam.c` (skalowanie jakości, domyślne tablice, `jpeg_simple_progression`), `jccolor.c`, `jcsample.c`, `jfdctint.c` (FDCT islow), `jcdctmgr.c` (kwantyzacja z zaokrąglaniem), `jchuff.c`, `jcphuff.c`, `jcmarker.c`. Licencja IJG jest już w NOTICES.
- Konwersja RGB→YCbCr (tablice stałoprzecinkowe z `jccolor.c`) i Gray dla obrazów w skali szarości.
- Podpróbkowanie chrominancji 4:4:4, 4:2:2, 4:2:0 (domyślnie) i 4:4:0, z uśrednianiem i naprzemiennym zaokrąglaniem jak `h2v1_downsample`/`h2v2_downsample`. Brzegi uzupełniane replikacją do pełnych MCU.
- FDCT islow i kwantyzacja bit-exact z libjpeg-turbo.
- Tablice kwantyzacji z Annex K skalowane jakością 1–100 (`jpeg_quality_scaling`, domyślnie 75), z wymuszeniem wartości baseline ≤255.
- Kodowanie Huffmana:
  - domyślne tablice z Annex K;
  - opcjonalnie tablice optymalne (`OptimizeCoding`: dwa przebiegi i `jpeg_gen_optimal_table`);
  - opcjonalnie restart markers (`RestartInterval`).
- Tryb progresywny według skryptu `jpeg_simple_progression` (DC first/refine, AC first/refine z EOB runs). Wymusza tablice optymalne, tak jak libjpeg.
- Zapis markerów: SOI, APP0 JFIF z DPI, APP1 EXIF, APP2 ICC (dzielony na kawałki ≤65519 B), APP1 XMP, DQT, SOF0/SOF2, DHT, DRI, SOS, EOI. Kolejność i nagłówki jak w Pillow.
- Alfa: JPEG jej nie ma, więc piksele półprzezroczyste składamy na `BackgroundColor` (domyślnie białe).
- API:
  - `JpegEncoderOptions`: `Quality`, `Subsampling`, `Progressive`, `OptimizeCoding`, `RestartInterval`, `BackgroundColor`, `Metadata`;
  - `SaveAsJpeg(path, quality)`, `ToJpeg(options)`;
  - `Image.Save` rozpoznaje `.jpg`/`.jpeg`;
  - `ImageFormat.Jpeg.CanEncode` staje się `true`.
- Testy:
  - porównanie bajtów z Pillow (`Image.save(quality, subsampling, optimize, progressive)`) dla źródeł z korpusu i różnych opcji;
  - nasze pliki dekodowane przez nasz dekoder i Pillow dają te same piksele;
  - metadane przechodzą round-trip;
  - fuzz opcji: walidacja zakresów rzuca `ArgumentOutOfRangeException`.
- **DoD:**
  - dla tych samych pikseli i opcji wynik jest bajt w bajt równy plikowi z Pillow (libjpeg-turbo 2.1.5); kryterium zastępcze: identyczne dane skanu (segmenty od DQT do EOI) przy dopuszczalnych różnicach w markerach APP;
  - Pillow, ImageMagick i nasz dekoder otwierają wszystkie nasze pliki;
  - PSNR i rozmiar dla q=75 i 90 równe Pillow.

### Faza 6: enkoder lossless VP8L (ok. 5k LOC)
**Status: ukończona (2026-10-13), na bazie libwebp 1.6.0.**
Weryfikacja: `dotnet build -c Release` (0 ostrzeżeń/błędów), `dotnet test -c Release --no-build` (2012/2012, w tym 87 nowych testów enkodera).
- 9 syntetycznych źródeł PNG: identyczne RGBA po dekodowaniu naszym dekoderem i `dwebp` 1.6.0; wszystkie rozmiary mieszczą się w limicie `cwebp -lossless -exact -q 75 -m 4` +1%. Jest to kryterium zastępcze DoD; pełna zgodność bajtowa z `cwebp` nie jest deklarowana.
- Testy metod 0–6 i jakości 0/25/75/100, granic palety i wymiarów, długich powtórzeń, color cache, `Exact`, poziomów `NearLossless`, limitów Huffmana i kosztów stałoprzecinkowych zgodnych z 1.6.0.
- Zapis statycznego VP8L przez `Image.Save`/`SaveAsWebP`/`ToWebP`, `WebP.Encode`, `WebP.EncodeLosslessRgba` i `WebP.Convert`; ICC/EXIF/XMP, padding RIFF, strumienie bez seek, walidacja przed zapisem. Tryb VP8L wybiera `Lossless = true`; enkoder lossy opisano w fazie 7, a zapis animacji pozostaje w fazie 8.
- Tryby transformacji i referencje LZ77 są porównywane według rzeczywistego rozmiaru strumienia. Port jest skalarny; dalsza optymalizacja szybkości i alokacji pozostaje w fazie wydajnościowej.
- Przejście wyroczni na 1.6.0 nie zmieniło 248 wpisów `reference.json`; źródła i narzędzia natywne pozostają wyłącznie w ignorowanym `reference/`.

Źródła: `src/enc/vp8l_enc.c`, `backward_references_enc.c`, `backward_references_cost_enc.c`, `histogram_enc.c`, `predictor_enc.c`, `near_lossless_enc.c`, `src/utils/huffman_encode_utils.c`, `bit_writer_utils.c`, `src/dsp/lossless_enc.c`.
- Analiza i wybór trybu entropii, budowa i sortowanie palety.
- Transformacje: subtract-green, predictor (wybór trybu per kafel), cross-color.
- LZ77: hash chain, RLE, cost-based TraceBackwards, mapowanie odległości 2D.
- Dobór rozmiaru color cache.
- Histogramy i klastrowanie: entropy binning, stochastic combine, greedy combine.
- Kody Huffmana o długości ≤15 wraz z kodowaniem długości kodów.
- Mapowanie `Quality`/`Method` na parametry wysiłku jak w libwebp. Opcje `Exact` i `NearLossless`.
- **DoD:**
  - encode→decode zwraca identyczne piksele (naszym dekoderem i `dwebp`),
  - rozmiar pliku ≤ `cwebp -lossless` +1%, docelowo identyczne bajty.

### Faza 7: enkoder lossy VP8 i alfa (ok. 6k LOC)
**Status: ukończona (2026-10-13), na bazie libwebp 1.6.0.**
Weryfikacja: `dotnet build -c Release --no-restore` (0 ostrzeżeń/błędów), `dotnet test -c Release --no-build` (2023/2023).
- Skalarne DCT/WHT, predykcje, kwantyzacja, koszty, zapis bitów i tokenów porównane dokładnie z natywnymi funkcjami. Analiza i rekonstrukcja 3360 makrobloków zgodne z referencją; RGB→YUV oraz SharpYuv sprawdzone także dla nieparzystych i małych wymiarów.
- 9 źródeł PNG × jakości 0/35/75/100 × metody 0–6: wszystkie 252 kombinacje spełniają DoD. Obrazy nieprzezroczyste są bajtowo zgodne z `cwebp`; przy różnicach w kompresji ALPH piksele po `dwebp` są identyczne, a rozmiar nie przekracza +1%.
- 116 porównań ustawień presetów, SNS, filtrów, segmentów, wieloprzebiegowego sterowania rozmiarem/PSNR, alfy, `Exact` i SharpYuv. 20 konfiguracji partycji zgodnych bajtowo z natywnym API; jak w libwebp, metody 3–6 używają jednej partycji tokenów.
- Publiczne API zapisuje statyczne VP8/VP8X z ALPH oraz ICC/EXIF/XMP. Domyślne opcje WebP działają (lossy q75/m4); walidacja opcji i wymiarów odbywa się przed zapisem.
- Uzupełniono próby VP8L dla metody 6/jakości 100: wszystkie transformacje również przy małych paletach, wariant bez cache, sortowanie palety Modified Zeng oraz LZ77 Box. Biblioteka pozostaje bez zależności i kodu natywnego; zapis animacji pozostaje w fazie 8.

Źródła: `src/enc/webp_enc.c`, `config_enc.c`, `picture_csp_enc.c`, `analysis_enc.c`, `quant_enc.c`, `frame_enc.c`, `tree_enc.c`, `token_enc.c`, `syntax_enc.c`, `filter_enc.c`, `cost_enc.c`, `iterator_enc.c`, `alpha_enc.c`, `picture_tools_enc.c`, `src/dsp/enc.c`, `cost.c`, `src/utils/quant_levels_utils.c`, opcjonalnie `sharpyuv/`.
- RGB→YUV420.
- Analiza makrobloków i k-means do maksymalnie 4 segmentów.
- Przeliczenie quality→q z modulacją SNS.
- Decyzje trybów z RD-opt zależnym od `Method`, trellis dla m≥5.
- Zapis tokenów, aktualizacja prawdopodobieństw, skip proba.
- Dobór siły filtra, partycje.
- Wieloprzebiegowe `TargetSize`/`TargetPsnr`.
- Czyszczenie RGB pod przezroczystymi pikselami (jak cwebp, chyba że ustawiono `Exact`).
- ALPH: filtr none/fast/best, kwantyzacja `AlphaQuality`, kompresja przez enkoder VP8L.
- Zapis kontenera VP8/VP8X.
- Presety z `config_enc.c`.
- Na końcu SharpYuv.
- **DoD:**
  - dla tego samego wejścia PNG i flag (`-q`, `-m`, `-alpha_*`) wynik jest bajt w bajt równy `cwebp` 1.6.0 lub, gdzie to niemożliwe, PSNR ≥ cwebp −0,05 dB przy rozmiarze ≤ +1%;
  - `dwebp` dekoduje wszystkie nasze pliki.

### Faza 8: enkoder animacji i muxer (ok. 1,5k LOC)
**Status: ukończona (2026-10-13), na bazie libwebp 1.6.0.**
Weryfikacja: `dotnet build -c Release --no-restore` (0 ostrzeżeń/błędów), `dotnet test -c Release --no-build` (2069/2069, 0 pominiętych). CLI poprawnie przekodował animację korpusu; webpmux i Pillow otworzyły wynik. Pakiet NuGet ma zero zależności i komplet not copyright.
- Enkoder ANIM/ANMF dostępny przez `Image.Save`/`SaveAsync`/`ToWebP`, fasadę WebP i CLI. Prostokąty zmian mają parzyste offsety; rozważamy dispose none/background i blend/no-blend, a `AllowMixed` także VP8/VP8L.
- Wybór klatki kluczowej według najmniejszego kosztu w oknie po `KeyframeMin` do `KeyframeMax`, z normalizacją jak w libwebp. `KeyframeMax = 0` wyłącza wstawianie, `1` wymusza keyframes. `MinimizeSize` wyłącza periodyczne keyframes i dodatkowo próbuje pełne płótna.
- Wspólny muxer statycznych obrazów i animacji zapisuje VP8X, ICCP, ANIM/ANMF, EXIF/XMP, rozmiary i padding RIFF. Przed zapisem walidujemy wymiary, pętle (0–65535) i czasy (0–16777215 ms, zaokrąglenie do ms). Powtórzone klatki zachowują osobne czasy.
- 46 nowych testów obejmuje lossless/lossy/mixed, alfa i ukryte RGB, małe/nieparzyste/graniczne wymiary, dispose/blend, długie sekwencje i losowe częściowe aktualizacje, keyframes, metadane, pliki/strumienie bez seek i async. Testy Pillow/webpmux obejmują 38 konfiguracji, w tym 7 animacji korpusu; porównujemy wszystkie skomponowane RGBA, czasy, pętle i tło, a EXIF odzyskujemy przez webpmux.
- Obecny limit płótna: 16383 na wymiar dla trybu lossy, 16384 dla lossless/mixed. Pełna zgodność bajtowa/rozmiarowa z img2webp nie jest kryterium tej fazy.

Źródła: `src/mux/anim_encode.c`, `muxedit.c`, `muxinternal.c`.
- Dla każdej klatki: prostokąt zmian względem poprzedniego płótna (offsety parzyste).
- Kandydaci dispose none/background × blend/no-blend, a przy `AllowMixed` także lossy kontra lossless. Wybieramy najmniejszy wynik.
- Keyframes według `KeyframeMin`/`KeyframeMax`.
- Zapis ANIM/ANMF oraz ICCP/EXIF/XMP we właściwej kolejności i z poprawnymi flagami VP8X.
- **DoD:**
  - round-trip animacji przez nasz dekoder oraz Pillow,
  - `webpmux -info` poprawnie listuje klatki,
  - `webpmux -get exif` zwraca nasz EXIF.

### Faza 9: przetwarzanie obrazu w `Image` (ok. 2k LOC)
**Status: ukończona (2026-10-13), resize zgodny z Pillow 10.2.0 piksel w piksel.**
Weryfikacja: `dotnet build RasterSharp.slnx -c Release` (0 ostrzeżeń/błędów), `dotnet test -c Release --no-build` (2151/2151, 0 pominiętych).
- `Processing/Resizer.cs` portuje Pillow: `Resample.c` (wagi double → 22-bitowe stałoprzecinkowe, przebieg poziomy tylko dla potrzebnych wierszy, potem pionowy, pośredni bufor 8-bit), `Convert.c` (RGBA → RGBa → RGBA jak `Image.resize`) i `Geometry.c` `ImagingScaleAffine` dla Nearest (bez premultiplikacji, akumulacja współrzędnych w double). Wiersze idą przez `Parallel.For` powyżej progu pracy; sumy całkowite nie zależą od kolejności, więc wynik jest deterministyczny.
- `ResizeMode` odwzorowuje `ImageOps`: Fit = `contain` (zaokrąglenie half-to-even, minimum 1 px tam, gdzie Pillow rzuca wyjątek), Fill = `fit` (ułamkowy box źródła w `float`), Pad = `pad` (domyślnie czarny przezroczysty, overload z kolorem). Do tego `Thumbnail` (Fit, nigdy nie powiększa) i `GetThumbnailImage` (nowy obraz).
- `Grayscale` (luma BT.601 jak `L` w Pillow), `Invert`, `Clear` (ustawia `HasAlpha`), `Fill` (nadpisanie, przycięcie do granic), `MakeTransparent()`/`MakeTransparent(Color)`, `DrawImage` (port `AlphaComposite.c`, krycie skaluje alfę źródła, przycinanie, rysowanie obrazu na sobie samym).
- Wyrocznia `tests/assets/processing_reference.py`: 277 zadań identycznych z Pillow (4 filtry × 4 tryby na szumie z alfą, pasku 100×3, zdjęciu i PNG z alfą; `alpha_composite`, grayscale, `exif_transpose` dla orientacji 1–8). Lokalnie także zdjęcie 12 MP (Fill Lanczos3, upscale Bicubic) zgodne bajt w bajt.
- Testy właściwości: wszystkie 8 wariantów RotateFlip kontra naiwny obrót + flip, 4×obrót/2×flip = tożsamość, AutoOrient dla 8 orientacji EXIF, granice Crop/Clone, jednolity kolor i brak przecieku koloru spod alfy 0, wszystkie klatki animacji, LockBits/CopyPixels/LoadPixels dla każdego `PixelFormat` (ze stride).
- Ograniczenie (jak w Pillow): kolory półprzezroczyste przechodzą przez 8-bitową premultiplikację i obcinające dzielenie, np. (200,100,50,α=77) → (198,99,49). Kolory nieprzezroczyste są zachowane dokładnie.
- `Resizer`: separowalny splot dwuprzebiegowy z wagami liczonymi z góry, na alfie premultiplikowanej. Filtry Nearest, Bilinear, Bicubic (Catmull-Rom), Lanczos3. Pętla po wierszach przez `Parallel.For`.
- Tryby `ResizeMode`: Stretch, Fit, Fill (z przycięciem), Pad.
- `Crop`, `RotateFlip` (8 wariantów), `AutoOrient`.
- `Grayscale` (BT.601), `Invert`, `Clear`/`Fill`, `MakeTransparent`, `DrawImage` (source-over z krycie).
- Wszystkie operacje działają na wszystkich klatkach.
- **DoD:**
  - testy właściwości (4×Rotate90 = tożsamość, dwukrotny flip = tożsamość, Crop ma poprawne granice),
  - resize zgodny z Pillow z tolerancją,
  - LockBits/CopyPixels round-trip dla każdego `PixelFormat`.

### Faza 9.5: API w stylu ImageSharp i filtry Pillow
**Status: ukończona (2026-10-13).**
Weryfikacja: `dotnet build RasterSharp.slnx -c Release` (0 ostrzeżeń/błędów), `dotnet test -c Release` (wszystkie zielone, 0 pominiętych).
- Cel: intuicyjne API odkrywalne z IntelliSense, jedna oczywista droga dla każdej czynności (przegląd: ImageSharp 3/4, sharp/NetVips, Pillow, nowości C# 14/.NET 10). API łamiemy bez okresu przejściowego (0.x, przed NuGet).
- **Wczytywanie/zapis:** `Load`/`LoadAsync`/`Identify(Async)`/`DetectFormat`/`LoadPixelData`; `Save`/`SaveAsync`; per format bloki `extension(Image)`: `SaveAsX(path|Stream, options?)`, `SaveAsXAsync`, `ToX`.
- **Przetwarzanie:** `Mutate(x => ...)` w miejscu, `Clone(x => ...)` jako nowy obraz z leniwą kopią (`Processing/ProcessingState.cs`: operacje tworzące nowy bufor czytają źródło bez kopiowania). Kontekst `ImageProcessingContext` to `readonly ref struct`; własne operacje dopisuje się blokiem `extension(ImageProcessingContext)`.
- **Usunięte artefakty System.Drawing:** `From*`, `RawFormat`/`MemoryBmp`, `Tag`, `ICloneable`, aktywna klatka, `GetPixel/SetPixel`, `LockBits/BitmapData/ImageLockMode`, `RotateFlipType`, `GetThumbnailImage`, `Clone(Rectangle)`, `SetResolution` (DPI w `ImageMetadata`).
- **Resize:** 0 = proporcje, `ResizeOptions` (`Position` → Pillow `centering`, `PadColor`, `AllowUpscale`), tryby w nazewnictwie ImageSharp (Stretch/Max/Crop/Pad/Min), filtry `Box`/`Hamming`.
- **Nowe operacje:** `Crop(w, h, anchor)`, `Pad`, `Rotate/Flip` (`RotateMode`/`FlipMode`), `Rotate(stopnie)` (port `Image.rotate` + `Geometry.c`: `affine_fixed`, `bilinear/bicubic_filter32RGB`), `Brightness`/`Contrast`/`Saturate` (`ImageEnhance` + `Blend.c`), `BoxBlur`/`GaussianBlur` (`BoxBlur.c`), `UnsharpMask` (`UnsharpMask.c`), `Sharpen` (`Filter.c` 3×3), `Sepia`, `BackgroundColor`, `Opacity`, `DrawImage(anchor)`, `Apply`.
- Filtry przestrzenne i obrót z interpolacją działają na alfie premultiplikowanej, jak Pillow na „RGBa” — bez przecieku koloru spod przezroczystych pikseli.
- **Kolor i opcje:** jeden typ koloru w sygnaturach (`Rgba32`, `Color` niejawnie), `Rgba32.Parse/TryParse/ToHex`; opcje jako rekordy walidowane przy ustawianiu.
- **DoD:**
  - wszystkie dotychczasowe testy przepisane na nowe API i zielone (te same hashe kodeków i wyniki resize),
  - nowe filtry, obrót i kotwice piksel w piksel z Pillow 10.2.0 (wyrocznia: 569 zadań),
  - migawka publicznego API `tests/assets/PublicApi.approved.txt` zatwierdzona (`PublicApiTests`),
  - przykłady z README i `docs/` kompilują się i działają (`DocsExamplesTests`).

### Faza 9.9: GIF, odczyt i zapis z animacją, konwersja GIF↔WebP (ok. 3k LOC)
**Status: ukończona (2026-10-13), wydana jako 0.2.0.**
Weryfikacja: `dotnet build RasterSharp.slnx -c Release` (0 ostrzeżeń/błędów), `dotnet test -c Release` (2682/2682, w tym 439 testów GIF; testy `Category=Oracle` uruchomione z `gif2webp`/`anim_dump` 1.6.0 zbudowanymi z giflib).
- Dekoder: 30 plików korpusu (Pillow, ImageMagick, ręcznie złożone: deferred clear, kod 8 bitów przy małej palecie, palety lokalne, brak GCE, disposal background→previous, rozszerzenia, ekran 0×0, brak palety, indeksy poza paletą, śmieci zamiast trailera) zgodnych piksel w piksel z `anim_dump` 1.6.0; w jednym przypadku (`hand_bg_then_previous`) `anim_util.c` przywraca niewłaściwe płótno, więc referencją jest `gif2webp`, a pliki odrzucane przez libwebp mają referencję z Pillow. Czasy, pętla, tło, XMP/ICC i `Identify` zgodne z referencją. Testy obcięć, błędnych kodów, przycinania klatek poza ekranem, limitu `MaxPixels` dla wszystkich klatek i 900 losowych mutacji (wyłącznie `ImageFormatException`).
- Kwantyzacja: port `Quant.c`/`QuantHash.c`/`QuantHeap.c` (median cut), `QuantOctree.c` i ditheringu FS z `Convert.c`/`Palette.c`; 147 wyników (7 źródeł, w tym >65536 kolorów, × 7 liczb kolorów × 3 metody) identycznych z Pillow 10.2.0 co do palety i indeksów, a zapis obrazów statycznych daje te same piksele co `Image.quantize`.
- Enkoder: round-trip bez strat dla wszystkich GIF-ów korpusu (także z przeplotem i bez optymalizacji), animacji WebP z ≤256 kolorami i 40 losowych animacji z przezroczystością; palety: wspólna dokładna, lokalne dokładne (tylko kolory rysowanych pikseli), a w ostateczności wspólna kwantyzowana. Nasze pliki dekodują się identycznie w `anim_dump` i Pillow (3 zestawy opcji × 39 wejść).
- Konwersja: `WebP.Convert` daje klatki, czasy, pętlę, tło i XMP identyczne z `gif2webp` 1.6.0 dla 27 plików korpusu (pozostałe 3 `gif2webp` odrzuca albo czyta inaczej z wyboru); `webpmux -info` poprawnie listuje klatki; GIF→WebP lossless→GIF zachowuje piksele, czasy i pętlę; `gif2webp` czyta nasze GIF-y z tymi samymi klatkami.
- Rozmiar względem Pillow `save_all=True, optimize=True`: 6 z 7 animacji korpusu WebP mniejszych (0,26–0,60×). Wyjątek `alpha_lossy` (6040 B wobec 1607 B): zapisujemy ją bezstratnie z lokalnymi paletami, a Pillow redukuje ją do 8 kolorów i psuje przezroczystość (poprawny wariant Pillow z `disposal=2` ma 2020 B). Kryterium „≤ Pillow +10%” spełnione dla przypadków bez utraty jakości; `ColorCount = 64` daje 1866 B.
- Rozbieżności semantyki opisane w testach: Pillow powiększa płótno do klatek wychodzących poza ekran i inaczej usuwa klatki (tłem z palety), `gif2webp` zostawia klatki bez palety przezroczyste (my: skala szarości jak Pillow).

Specyfikacje: GIF89a (i GIF87a), rozszerzenia aplikacji NETSCAPE2.0/ANIMEXTS1.0 (pętla), XMP DataXMP, ICCRGBG1 (ICC). Algorytmy referencyjne: libwebp 1.6.0 `examples/gifdec.c`, `gif2webp.c`, `anim_util.c` (`ReadAnimatedGIF`); Pillow 10.2.0 `GifImagePlugin.py`, `GifDecode.c`, `GifEncode.c`, `Quant.c` (licencja HPND jest już w NOTICES). LZW piszemy sami, bez giflib.
- Wykrywanie po sygnaturze `GIF87a`/`GIF89a`. `ImageFormat.Gif` (`image/gif`, `.gif`) rejestrujemy w `CodecRegistry`.
- Dekoder GIF:
  - logical screen descriptor, globalna i lokalne tablice kolorów (2–256 wpisów), kolejne bloki obrazu i rozszerzeń;
  - LZW: minimalny rozmiar kodu 2–8, kody do 12 bitów, clear/EOI, „deferred clear” po 4096 kodach, dane w sub-blokach;
  - przeplot (4 przebiegi);
  - Graphic Control Extension: disposal 0–3 (wartości 4–7 traktujemy jak 0), indeks przezroczystości, opóźnienie (×10 ms, bez zmian w `ImageFrame.Duration`);
  - NETSCAPE2.0/ANIMEXTS1.0 → `LoopCount` (model WebP, czyli liczba odtworzeń, 0 = nieskończenie): brak rozszerzenia → 1, n>0 → n+1 (z limitem 65535), 0 → 0, jak `gif2webp`;
  - XMP (DataXMP) i ICC (ICCRGBG1) trafiają do `ImageMetadata`. Komentarze, Plain Text i nieznane rozszerzenia pomijamy;
  - kompozycja klatek jak w `anim_util.c` `ReadAnimatedGIF` (semantyka przeglądarek):
    - płótno startuje przezroczyste;
    - dispose background czyści prostokąt do przezroczystości, a dispose previous przywraca poprzednią zawartość;
    - klatki wychodzące poza ekran logiczny przycinamy;
    - przezroczyste piksele mają postać (0,0,0,0);
  - `AnimationBackgroundColor` z indeksu tła, jak `GIFGetBackgroundColor` (przezroczysty, gdy równy indeksowi przezroczystości);
  - `Identify` bez dekodowania LZW: wymiary, `FrameCount`, `IsAnimated`, `HasAlpha` (z GCE);
  - opcje `FirstFrameOnly` i `MaxPixels`, z budżetem pamięci klatek jak w animacjach WebP/APNG;
  - tryb tolerancyjny jak w Pillow: obcięte dane LZW i indeksy spoza palety nie wywalają dekodera. Dane, których nie da się odczytać, dają wyłącznie `ImageFormatException`.
- Enkoder GIF (`GifEncoderOptions`):
  - `ColorCount` (2–256, domyślnie 256);
  - `Quantizer` (Auto/MedianCut/Octree);
  - `Dither` (None/FloydSteinberg, domyślnie None);
  - `TransparencyThreshold` (0–255, domyślnie 128: alfa poniżej progu daje piksel przezroczysty, pozostałe są nieprzezroczyste);
  - `Interlace`;
  - `SaveAnimation` (domyślnie true);
  - `OptimizeFrames` (domyślnie true);
  - `Metadata` (XMP i ICC jako rozszerzenia aplikacji; EXIF nie istnieje w GIF).
- Paleta i kwantyzacja:
  - dokładna paleta, gdy kolorów (po progu alfy) jest ≤ `ColorCount`: kolory nieprzezroczyste zachowujemy bit w bit;
  - w przeciwnym razie port median cut z Pillow `Quant.c` (indeksy jak `Image.quantize(method=MEDIANCUT)`), opcjonalnie octree, oraz dithering Floyd–Steinberga jak w `Convert.c`;
  - jeden wpis rezerwujemy na przezroczystość;
  - paleta globalna, gdy wszystkie klatki się w niej mieszczą, w przeciwnym razie lokalne tablice klatek.
- Enkoder LZW ze słownikiem 4096 kodów i clear po zapełnieniu. Sub-bloki po 255 B.
- Animacja:
  - czasy ms → setne sekundy z zaokrągleniem (udokumentować utratę precyzji);
  - `LoopCount` odwrotnie niż w dekoderze: 1 → brak NETSCAPE2.0, 0 → 0, n → n−1;
  - `OptimizeFrames`, wzorowane na `_write_multiple_frames` w Pillow:
    - prostokąt zmian względem poprzedniego płótna;
    - niezmienione piksele kodujemy indeksem przezroczystości;
    - dobór disposal none/background, gdy piksele stają się przezroczyste.
- API:
  - `SaveAsGif`/`SaveAsGifAsync`/`ToGif` (blok `extension(Image)`), `Image.Save` rozpoznaje `.gif`;
  - `ImageFormat.Gif.CanEncode = true`;
  - migawka `PublicApi.approved.txt`.
- Konwersja:
  - GIF→WebP przez `Image.Load(...).SaveAsWebP(...)`, `WebP.Convert` (przyjmuje `.gif`) i CLI `convert anim.gif anim.webp`;
  - `WebP.Convert` i CLI dla wejścia GIF bez jawnych opcji używają domyślnych ustawień `gif2webp`: lossless, kmin/kmax 9/17 (lossy 3/5) i czasy ≤10 ms → 100 ms;
  - WebP→GIF przez `SaveAsGif` i CLI `convert anim.webp anim.gif`;
  - `docs/` (animation, loading-and-saving, encoder-options, cli) z przykładami testowanymi przez `DocsExamplesTests`.
- Korpus (`generate_corpus.py`):
  - Pillow: `save_all`, disposal 0–3, przezroczystość, palety lokalne, przeplot, pętle 0/1/n i brak NETSCAPE, czasy 0/10/20/…;
  - ImageMagick: GIF87a, `-dispose`, `-layers optimize`, `-interlace GIF`;
  - pliki ręczne: kod 2 i 8 bitów, deferred clear, klatki poza ekranem, obcięte LZW, brak GCT, indeks poza paletą, rozszerzenia comment/plain text/XMP/ICC.
- Wyrocznie:
  - `make_reference.py` liczy hashe skomponowanych klatek z `anim_dump` 1.6.0 oraz Pillow;
  - `scripts/build-webp-reference.sh` buduje `gif2webp`/`anim_dump`/`anim_diff`, gdy jest giflib (`libgif-dev` instalujemy za zgodą użytkownika);
  - testy `Category=Oracle` są pomijane bez narzędzi.
- **DoD:**
  - dekoder: wszystkie GIF-y z korpusu mają klatki identyczne z `anim_dump` 1.6.0 (piksele, a czasy po normalizacji ≤10 ms → 100 ms) i z Pillow tam, gdzie semantyka jest wspólna; różnice semantyki Pillow są opisane w testach. Uszkodzone pliki dają tylko `ImageFormatException`/`NotSupportedException`;
  - enkoder:
    - obrazy i animacje z ≤256 kolorami i alfą binarną przechodzą round-trip bez strat;
    - kwantyzacja median cut daje indeksy identyczne z Pillow;
    - nasze pliki otwierają Pillow, ImageMagick i `gif2webp` z pikselami równymi naszemu dekoderowi;
    - rozmiar animacji ≤ Pillow `save_all=True, optimize=True` +10%;
  - konwersja:
    - GIF→WebP przez `WebP.Convert` daje klatki (po `anim_dump`/Pillow), czasy i pętlę identyczne z wynikiem `gif2webp` 1.6.0, a `webpmux -info` poprawnie listuje klatki;
    - WebP→GIF dla 7 animacji korpusu daje klatki zgodne ze źródłem po progu alfy i kwantyzacji (dokładnie przy ≤256 kolorach);
    - round-trip GIF→WebP lossless→GIF zachowuje piksele, czasy i pętlę.

### Faza 10: wydajność, odporność, wydanie
- BenchmarkDotNet: dekodowanie i kodowanie w porównaniu z czasami `cwebp`/`dwebp`. Cel: ≤3× wolniej od natywnego C z SIMD.
- Ścieżki `Vector128`/`Vector256` (cross-platform intrinsics) dla:
  - IDCT/FDCT,
  - loop filter,
  - YUV↔RGB,
  - transformacji lossless,
  - SSE/kosztów,
  - resize,
  - inflate/deflate CRC.

  Każda ścieżka SIMD ma test, że daje **wynik identyczny ze skalarnym**.
- Równoległość enkodera (analiza, próby lossless) i dekodera (filtrowanie wierszy). Wynik musi być deterministyczny.
- Testy odporności: obcinanie i losowe mutacje korpusu. Wolno wylecieć wyłącznie `ImageFormatException` lub `NotSupportedException`; sprawdzamy też `MaxPixels`.
- Dokumentacja XML, README z przykładami, CLI sample, pakiet NuGet (bez zależności), THIRD-PARTY-NOTICES.

## Wytyczne portowania z C
- Portujemy funkcja po funkcji, z komentarzem źródła, np. `// port: src/dec/vp8_dec.c GetCoeffsFast`.
- Najpierw wersja skalarna z referencyjnych plików `dsp/*.c`. SIMD dopiero w fazie 10.
- Pilnujemy semantyki C:
  - promocja do `int`,
  - rzutowania `(uint8_t)` przez `clip_8b`,
  - przesunięcia arytmetyczne,
  - mieszanie `uint`/`int`,
  - zmiennoprzecinkowe obliczenia w `float` muszą zostać w `float`, z tą samą kolejnością operacji (warunek bit-exact z cwebp).
- Ścieżki inkrementalne (`idec_dec.c`) i wielowątkowe pomijamy jako rzeczy wtórne.
- Rzeczy pominięte na początku: dithering przy dekodowaniu, `alpha_dither`, rescaler libwebp (resize robi nasz `Resizer`).

## Weryfikacja end-to-end
```bash
dotnet build -c Release
dotnet test -c Release                               # unit, zgodność z reference.json, round-tripy
dotnet test -c Release --filter Category=Oracle      # porównania z cwebp/dwebp/webpmux (pomijane bez narzędzi)
dotnet run -c Release --project samples/RasterSharp.Cli -- convert photo.jpg out.webp -q 80
dwebp out.webp -o check.png && webpmux -info out.webp
dotnet run -c Release --project samples/RasterSharp.Cli -- convert anim.gif anim.webp   # GIF↔WebP (faza 9.9)
dotnet run -c Release --project benchmarks/RasterSharp.Benchmarks
```

Rodzaje testów:
1. **Prymitywy:** bit reader/writer, Huffman, IDCT kontra wersja float, CRC/Adler na znanych wektorach, inflate kontra `ZLibStream`.
2. **Zgodność dekoderów:** hashe pikseli z `reference.json` (dwebp i Pillow), libwebp-test-data, PngSuite, BMP Suite, klatki GIF z `anim_dump` 1.6.0 i Pillow.
3. **Konwersja GIF↔WebP:** porównanie z `gif2webp` 1.6.0 (klatki, czasy, pętla), round-tripy przez nasz dekoder i Pillow.
4. **Enkodery:** porównanie bajtów i PSNR z `cwebp` 1.6.0, round-tripy lossless, metadane i animacje przez `webpmux` i Pillow.
5. **API `Image`:** właściwości, Mutate/Clone, operacje i filtry (wyrocznia Pillow), konwersje formatów pikseli, migawka publicznego API i przykłady z README oraz `docs/`.
6. **Odporność:** fuzz i mutacje korpusu.
