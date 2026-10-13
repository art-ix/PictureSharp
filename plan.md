# WebpSharp: plan implementacji

## Kontekst

Potrzebna jest biblioteka .NET (**net10.0+**) do kodowania i dekodowania WebP, napisana w 100% w kodzie zarządzanym (C#), **bez zależności NuGet i bez natywnych bibliotek**. Używamy tylko BCL, w tym `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`), które jest częścią shared framework i działa na każdej platformie. Biblioteka ma też czytać PNG, BMP i JPEG (do przekodowywania) oraz zapisywać PNG, BMP i JPEG. Centralnym typem jest klasa `Image`, wzorowana na `System.Drawing.Image`/`Bitmap`, z prostym API.

Ustalenia z użytkownikiem:
- **WebP w pełnym zakresie** (odczyt i zapis): lossy VP8, lossless VP8L, alfa ALPH, animacja ANIM/ANMF, metadane ICCP/EXIF/XMP.
- **PNG:** odczyt i zapis. **BMP:** odczyt i zapis. **JPEG:** odczyt i zapis (enkoder w fazie 5.5).
- **Deflate/zlib piszemy sami.** Nie używamy `ZLibStream`, bo pod spodem woła natywne zlib.
- **`Image` obejmuje:** skalowanie, geometrię (crop/rotate/flip/EXIF), dostęp do pikseli i proste operacje na kolorach.

Stan środowiska:
- Katalog projektu `/home/artur/Projekty/ai/webpsharp` jest pusty (nie jest repozytorium git).
- Jest SDK .NET 10.0.112.
- Narzędzia libwebp **1.3.2** (`cwebp`, `dwebp`, `webpmux`, `img2webp`), ImageMagick `convert` oraz Python 3 z Pillow 10.2. Wszystkie posłużą jako wyrocznie w testach.

**Kluczowa decyzja:** portujemy z tagu **libwebp v1.3.2**, czyli tej samej wersji co zainstalowane `cwebp`/`dwebp`.
- Dekoder można wtedy sprawdzać piksel w piksel z `dwebp`.
- Dla enkodera celujemy w wynik **bajt w bajt identyczny z `cwebp`** przy tych samych parametrach. Jeśli gdzieś się to nie uda (float/`pow`/`log`), kryterium zastępczym jest parytet PSNR i rozmiaru pliku.

---

## Struktura rozwiązania

```
webpsharp/
  WebpSharp.slnx
  Directory.Build.props          # net10.0, Nullable, LangVersion latest, TreatWarningsAsErrors,
                                 # IsAotCompatible, IsTrimmable, GenerateDocumentationFile
  THIRD-PARTY-NOTICES.txt        # licencja BSD-3 libwebp (kod portowany = praca pochodna) + inne źródła
  .gitignore                     # reference/, tests/assets/external/, bin/obj
  reference/libwebp/             # git clone --branch v1.3.2 (tylko do czytania, poza buildem)
  src/WebpSharp/                 # biblioteka, ZERO PackageReference
    Image.cs, ImageFrame.cs, ImageFrameCollection.cs, ImageInfo.cs, ImageFormat.cs,
    Rgba32.cs, PixelFormat.cs, ImageMetadata.cs, BitmapData.cs, enums (RotateFlipType,
    ResizeMode, ResamplingFilter, ImageLockMode, ExifOrientation), DecoderOptions.cs,
    EncoderOptions.cs (+ WebPEncoderOptions, PngEncoderOptions, BmpEncoderOptions), WebP.cs (fasada),
    ImageFormatException.cs
    Formats/
      FormatDetector.cs, IImageDecoder.cs, IImageEncoder.cs, CodecRegistry.cs (internal)
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
      Metadata/ (ExifReader/Patcher: Orientation, rozdzielczość; IccProfile)
    Compression/ (Inflater, Deflater, Adler32, Crc32)
    Processing/  (Resizer, Kernels, Geometry, ColorOps, Compositor, PixelConverter)
    Internal/    (BitHelpers, pooling, SIMD helpers)
  tests/WebpSharp.Tests/         # xUnit (zależności tylko w testach)
    assets/ generate_corpus.py, make_reference.py, fetch-external.sh, reference.json, corpus/, reference/
  benchmarks/WebpSharp.Benchmarks/ # BenchmarkDotNet
  samples/WebpSharp.Cli/         # `webpsharp convert in.jpg out.webp -q 80`, `info`, `resize`
```

Zasady kodu:
- Tylko jedna publiczna przestrzeń nazw `WebpSharp`. Wszystkie kodeki są `internal`.
- Tabele z C przenosimy jako `static ReadOnlySpan<T> X => [...]`, co daje dane bez alokacji.
- Zamiast `unsafe` używamy `Span`, `ref` i `Unsafe.Add`/`MemoryMarshal`.
- Bufory tymczasowe bierzemy z `ArrayPool<T>.Shared`. Piksele obrazu leżą w `GC.AllocateUninitializedArray<Rgba32>`.

---

## Publiczne API

### Typy podstawowe
- `Rgba32`: struktura `{byte R,G,B,A}` w układzie sekwencyjnym, z konwersjami do i z `System.Drawing.Color` oraz `uint`. Przechowuje **alfę prostą** (niepremultiplikowaną), tak samo jak WebP i PNG.
- `PixelFormat` (do eksportu i importu): `Rgba32, Bgra32, Argb32, Rgb24, Bgr24, Gray8, Rgba32Premultiplied, Bgra32Premultiplied`.
- `ImageFormat`: klasa z instancjami statycznymi `WebP`, `Png`, `Bmp`, `Jpeg`, `MemoryBmp`. Właściwości: `Name`, `MimeType`, `FileExtensions`, `CanDecode`, `CanEncode`.
- `ImageInfo` (wynik `Identify`, bez dekodowania pikseli): `Format`, `Width`, `Height`, `HasAlpha`, `FrameCount`, `IsAnimated`, `WebPCompression` (Lossy/Lossless/Mixed), `Orientation`, `HorizontalResolution`/`VerticalResolution`.
- `ImageMetadata`: `byte[]? IccProfile`, `Exif`, `Xmp`, `ExifOrientation Orientation`, `Clear()`.
- `ImageFormatException : FormatException`, rzucany dla uszkodzonych lub nierozpoznanych danych. Poprawne pliki z nieobsługiwanymi funkcjami (np. arytmetyczny JPEG) dają `NotSupportedException`.

### `Image` (sealed, `IDisposable`, `ICloneable`)

```csharp
// Wczytywanie
static Image FromFile(string path, DecoderOptions? o = null);
static Image FromStream(Stream s, DecoderOptions? o = null);     // nie wymaga Seek
static Image FromBytes(ReadOnlySpan<byte> data, DecoderOptions? o = null);
static Task<Image> FromFileAsync(string path, DecoderOptions? o = null, CancellationToken ct = default);
static Image FromPixels(ReadOnlySpan<byte> px, int w, int h, PixelFormat fmt, int stride = 0);
static ImageInfo Identify(string path) / Identify(Stream) / Identify(ReadOnlySpan<byte>);
static ImageFormat? DetectFormat(ReadOnlySpan<byte> header);
static Image CreateAnimation(IEnumerable<Image> frames, TimeSpan frameDuration, int loopCount = 0);
Image(int width, int height);  Image(int width, int height, Color background);

// Właściwości (w stylu System.Drawing)
int Width, Height;  Size Size;  ImageFormat RawFormat;  bool HasAlpha;
float HorizontalResolution, VerticalResolution;  void SetResolution(float x, float y);
ImageMetadata Metadata;  object? Tag;
// Animacja
ImageFrameCollection Frames;  int FrameCount;  bool IsAnimated;
int ActiveFrameIndex;  void SelectActiveFrame(int index);  ImageFrame ActiveFrame;
int LoopCount;  Color AnimationBackgroundColor;

// Piksele (aktywna klatka)
Color GetPixel(int x, int y);  void SetPixel(int x, int y, Color c);
Rgba32 this[int x, int y] { get; set; }
Span<Rgba32> GetRowSpan(int y);  Span<Rgba32> GetPixelSpan();
BitmapData LockBits(Rectangle r, ImageLockMode mode, PixelFormat fmt);  void UnlockBits(BitmapData d);
void CopyPixels(Span<byte> dest, PixelFormat fmt, int stride = 0);  byte[] ToPixelArray(PixelFormat fmt);
void LoadPixels(ReadOnlySpan<byte> src, PixelFormat fmt, int stride = 0);
bool IsOpaque();   // skan alfy

// Operacje: działają w miejscu na WSZYSTKICH klatkach i zwracają `this` (fluent)
Image Resize(int w, int h, ResizeMode mode = ResizeMode.Stretch, ResamplingFilter f = Lanczos3);
Image Thumbnail(int maxW, int maxH, ResamplingFilter f = Lanczos3);   // Fit, nigdy nie powiększa
Image Crop(Rectangle r);  Image RotateFlip(RotateFlipType t);  Image AutoOrient();
Image Grayscale();  Image Invert();  Image Clear(Color c);  Image Fill(Rectangle r, Color c);
Image MakeTransparent(Color c);  Image DrawImage(Image src, Point at, float opacity = 1f);
// Operacje zwracające NOWY obraz (zgodność z System.Drawing)
Image Clone();  Image Clone(Rectangle r);  Image GetThumbnailImage(int w, int h);

// Zapis
void Save(string path);                       // format z rozszerzenia (.webp -> lossy q75, .png, .bmp)
void Save(string path, ImageFormat f);  void Save(Stream s, ImageFormat f);
void Save(string path, EncoderOptions o);  void Save(Stream s, EncoderOptions o);
Task SaveAsync(...);
void SaveAsWebP(string path, int quality = 75, bool lossless = false);  void SaveAsPng(string path);  void SaveAsBmp(string path);
byte[] ToWebP(WebPEncoderOptions? o = null);  byte[] ToPng(PngEncoderOptions? o = null);  byte[] ToBmp(BmpEncoderOptions? o = null);
```

- **`ImageFrame`:** `Width`, `Height`, `Duration` (TimeSpan), indeksator `Rgba32`, `GetRowSpan`, `GetPixelSpan`, `Clone()`. Każda klatka to pełne płótno, a dekoder zwraca klatki już skomponowane, jak `WebPAnimDecoder`.
- **`ImageFrameCollection`:** `IReadOnlyList<ImageFrame>` plus `Add(Image, TimeSpan)`, `Insert`, `RemoveAt`, `Move`. Wymiary klatek muszą być równe wymiarom obrazu.
- **`BitmapData`:** `IDisposable` (Dispose = Unlock), ma `Width`, `Height`, `Stride`, `PixelFormat`, `Span<byte> Span` oraz przypięty `IntPtr Scan0` do interopu z WPF, SkiaSharp lub WinForms. Przy `ReadWrite`/`WriteOnly` dane są przy odblokowaniu konwertowane z powrotem do Rgba32.
- **`RotateFlipType`** ma wartości liczbowe identyczne z System.Drawing.
- **`AutoOrient()`** stosuje orientację EXIF 1–8 i ustawia tag na 1 bezpośrednio w bajtach EXIF.

### Opcje
- **`DecoderOptions`:**
  - `MaxPixels` (domyślnie 2^28)
  - `FirstFrameOnly`
  - `ApplyExifOrientation` (domyślnie false, tak jak w System.Drawing)
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

### Fasada `WebP` (odwzorowuje proste API z `decode.h`/`encode.h`)

```csharp
static bool TryGetInfo(ReadOnlySpan<byte> data, out ImageInfo info);
static byte[] DecodeRgba(ReadOnlySpan<byte> data, out int width, out int height);   // WebPDecodeRGBA
static byte[] DecodeBgra(...);  static void DecodeInto(ReadOnlySpan<byte> data, Span<byte> dst, int stride, PixelFormat f);
static byte[] EncodeRgba(ReadOnlySpan<byte> rgba, int w, int h, int stride, float quality);  // WebPEncodeRGBA
static byte[] EncodeLosslessRgba(ReadOnlySpan<byte> rgba, int w, int h, int stride);
static byte[] Encode(Image image, WebPEncoderOptions? o = null);
static void Convert(string inPath, string outPath, WebPEncoderOptions? o = null);   // jpg/png/bmp/webp -> webp
```

Przykład użycia:

```csharp
using var img = Image.FromFile("photo.jpg");
img.AutoOrient().Thumbnail(1920, 1080).SaveAsWebP("photo.webp", quality: 82);
```

### Wyraźne ograniczenia (do opisania w dokumentacji)
- Brak zarządzania kolorem: ICC jest przenoszony, ale nie stosowany. CMYK→RGB konwertujemy naiwnie.
- PNG 16-bit jest konwertowane do 8 bitów.
- BMP zapisujemy zawsze bez kompresji (RLE tylko przy odczycie). Animacje zapisujemy jako aktywną klatkę.
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
  - dla tego samego wejścia PNG i flag (`-q`, `-m`, `-alpha_*`) wynik jest bajt w bajt równy `cwebp` 1.3.2 lub, gdzie to niemożliwe, PSNR ≥ cwebp −0,05 dB przy rozmiarze ≤ +1%;
  - `dwebp` dekoduje wszystkie nasze pliki.

### Faza 8: enkoder animacji i muxer (ok. 1,5k LOC)
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
- `Resizer`: separowalny splot dwuprzebiegowy z wagami liczonymi z góry, na alfie premultiplikowanej. Filtry Nearest, Bilinear, Bicubic (Catmull-Rom), Lanczos3. Pętla po wierszach przez `Parallel.For`.
- Tryby `ResizeMode`: Stretch, Fit, Fill (z przycięciem), Pad.
- `Crop`, `RotateFlip` (8 wariantów), `AutoOrient`.
- `Grayscale` (BT.601), `Invert`, `Clear`/`Fill`, `MakeTransparent`, `DrawImage` (source-over z krycie).
- Wszystkie operacje działają na wszystkich klatkach.
- **DoD:**
  - testy właściwości (4×Rotate90 = tożsamość, dwukrotny flip = tożsamość, Crop ma poprawne granice),
  - resize zgodny z Pillow z tolerancją,
  - LockBits/CopyPixels round-trip dla każdego `PixelFormat`.

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
dotnet run -c Release --project samples/WebpSharp.Cli -- convert photo.jpg out.webp -q 80
dwebp out.webp -o check.png && webpmux -info out.webp
dotnet run -c Release --project benchmarks/WebpSharp.Benchmarks
```

Rodzaje testów:
1. **Prymitywy:** bit reader/writer, Huffman, IDCT kontra wersja float, CRC/Adler na znanych wektorach, inflate kontra `ZLibStream`.
2. **Zgodność dekoderów:** hashe pikseli z `reference.json` (dwebp i Pillow), libwebp-test-data, PngSuite, BMP Suite.
3. **Enkodery:** porównanie bajtów i PSNR z `cwebp` 1.3.2, round-tripy lossless, metadane i animacje przez `webpmux` i Pillow.
4. **API `Image`:** właściwości, operacje i konwersje formatów pikseli.
5. **Odporność:** fuzz i mutacje korpusu.
