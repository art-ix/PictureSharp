# PictureSharp: plan rozwoju (zrealizowany do 0.8.5, w toku 0.9.0)

## Stan i cel

**Ostatnie wydanie: 0.8.5 (2026-10-26).** PictureSharp to biblioteka obrazów dla .NET 10+ w C# 14, bez zależności i
kodu natywnego: WebP, PNG/APNG, JPEG, GIF, BMP, PCX i ICO, animacje (także sekwencyjnie), metadane, zarządzanie
kolorem, operacje graficzne, korekcja fotograficzna i negatywy, adaptery (System.Drawing, SkiaSharp, ASP.NET Core) i
odczyt oraz zapis HDR z JPEG z gain map.

Wtyczki mają osobne repozytoria i własny SemVer: `PictureSharp.Heif` (HEIC, własny port HEVC intra), `PictureSharp.Tiff`,
`PictureSharp.Formats.Extra`, JPEG XL. Weryfikacja Apple na macOS (0.6.6) czeka na dostęp do sprzętu.
CUR, TIFF, QOI, TGA, PNM i AVIF są poza rdzeniem (TIFF, QOI, TGA, PNM we wtyczkach).

Istniejące API i ograniczenia: `README.md`, `docs/`, XML docs, `tests/assets/PublicApi.approved.txt`, stabilne SPI
wtyczek: `docs/plugin-spi.md`. Pomiary: `docs/performance.md`. Ukończone wydania: [changelog](#changelog),
`docs/releases/` i historia Git. Lokalna historia pracy: `progres.md` (bez commitowania).

## Zasady obowiązujące we wszystkich wydaniach

- `net10.0`, `LangVersion=14.0`, nullable, warnings as errors i XML docs. Publiczne API w `PictureSharp` (model, opcje, konfiguracja i rozszerzenia `Image`), `PictureSharp.Processing`, `PictureSharp.Formats` (SPI wtyczek) i `PictureSharp.Formats.<Format>`; implementacja jest `internal`.
- Biblioteka używa wyłącznie BCL: bez `PackageReference`, P/Invoke, `unsafe` oraz `System.IO.Compression`. Deflate/inflate pochodzi z `Algorithms/Compression/` (migracja na `ZLibStream` zmierzona i wycofana, `docs/performance.md`, „Own deflate versus System.IO.Compression (0.7.2)”). `System.Drawing.Primitives` jest dozwolone. Wtyczki mogą używać `System.IO.Compression` do dekompresji.
- `Image` i `ImageFrame` przechowują `Rgba32` z prostą alfą; od 0.9.0 alternatywnie `Rgba64` według `PixelType` (wspólny dla wszystkich klatek), z zachowaniem 16-bitowej precyzji we wszystkich operacjach. Bufory robocze z `ArrayPool<T>`, piksele z `GC.AllocateUninitializedArray`; gorące pętle bez alokacji.
- Zachowujemy dotychczasowe wyniki i domyślne zachowanie. Nowe przekształcenia obrazu wymagają jawnej opcji. Nowe domyślne limity zasobów opisujemy jako zmianę zachowania.
- Uszkodzone dane: `ImageFormatException`; poprawne, nieobsługiwane warianty: `NotSupportedException`. Anulowanie: `OperationCanceledException`. Błędy I/O zachowują właściwy typ. Żadne złośliwe wejście nie może powodować niekontrolowanych wyjątków indeksowania lub przepełnienia.
- Nowe porty WebP bazują na read-only `reference/libwebp-1.6.0/`, z komentarzami źródłowymi i aktualizacją `THIRD-PARTY-NOTICES.txt`. Zachowujemy semantykę C, w tym kolejność operacji `float` i 23-bitowe koszty VP8L.
- Najpierw poprawna wersja skalarna. SIMD x64/ARM64 i równoległość dodajemy po pomiarach; wyniki muszą być identyczne ze skalarem i niezależne od liczby wątków.
- Testy, benchmarki i wyrocznie mogą używać zewnętrznych pakietów. Pobieranie zewnętrznych korpusów wymaga zgody użytkownika. Prywatne zdjęcia pozostają lokalne.
- Przed 1.0 wolno łamać API i SPI dla lepszego projektu: bez shimów zgodności, z podniesieniem `PluginVersionCheck` i migracją w `docs/plugin-spi.md`.
- Commit, push i publikacja wyłącznie na polecenie użytkownika. `VERSION` zmieniamy przy przygotowaniu wydania, nie przy rozpoczęciu etapu.

## Kolejność wydań

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.6.6 | Weryfikacja HDR z ImageIO/Core Image na macOS, usuwanie deklaracji Ultra HDR przy zapisie SDR | Odłożona do dostępności macOS |
| 0.6.9–0.8.5 | HDR przy edycji, SPI wtyczek, optymalizacje JPEG i WebP, korekcja fotograficzna, negatywy, gain map w enkoderach, generowanie map, stabilne SPI, `ImageInfo.Color` | Wydane (szczegóły w [changelogu](#changelog)) |
| 0.9.0 | 16 bitów na kanał w całym rdzeniu (`Rgba64`, `PixelType`, PNG 16-bit, wszystkie operacje) i konwersja kolorów we float na `Rgba64`; złamanie API i SPI z blokadą starych wtyczek | Bramka techniczna zaliczona (2026-10-28), przygotowanie wydania i scalenie do main zlecone przez użytkownika; wtyczki publikowane po rdzeniu; projekt w `docs/proposals/high-bit-depth.md` |

Numery określają kolejność, nie terminy. Każde wydanie wymaga własnego DoD oraz wspólnej bramki jakości.

## Otwarte weryfikacje

- **0.6.6 (macOS):** porównać rekonstrukcję wariantu Apple (i ISO) z ImageIO/Core Image na macOS/iOS, zapisać opcje, wersję
  systemu i tolerancje; benchmarki HDR względem ImageIO na tym samym sprzęcie; sprawdzić, czy zapis SDR nie przenosi
  nieaktualnych deklaracji HDR (np. XMP `hdrgm`). Brak środowiska Apple nie jest zaliczeniem: wariant Apple pozostaje
  „zgodny z referencjami Linux, niepotwierdzony na Apple”. Nie blokuje żadnego wydania (decyzja użytkownika 2026-10-17).
- **ARM64:** bramka 0.9.0 zaliczona na Orange Pi 5 (zgoda użytkownika w tym czacie): zestaw Category!=Local
  9303 testy, 0 błędów, 8 GDI+ pominiętych; bez intrinsics ten sam wynik, *SimdTests 138/138, smoke OK.
  Prywatne 48 MP HDR z Category=Local przekraczają limit sterty 4 GiB na tym urządzeniu; pełny zestaw Local zaliczony
  na x64. GDI+ nadal wymaga Windows. Szczegóły: docs/measurements/release-0.9.0-gate.json.
- **Kandydat SPI:** `ReportStoredSize` dla miniatur `thmb` (HEIF) czeka na pomiar zysku we wtyczce; bez zgłoszenia nie
  jest zobowiązaniem.
- **Wtyczki po 0.8.2 / 0.8.5:** przejście Tiff, Formats.Extra i Heif na `FormatMetadata` (hotspot XBM), opcje dekodera
  per wtyczka i `ImageInfo.Color` odbywa się w ich repozytoriach; test Heif `Modes_FollowTheJpegEncoder` do poprawy
  (mapa samego Apple jest od 0.8.1 zapisywana przez `GetGainMap` jako ISO).

## 0.9.0 — 16 bitów na kanał i ścieżka dekodowania float

Projekt: [docs/proposals/high-bit-depth.md](docs/proposals/high-bit-depth.md). Decyzja użytkownika (2026-10-28):
pełna obsługa 16 bitów na kanał w rdzeniu, wolno złamać publiczne API i stabilne SPI. Zastępuje zapis z 2026-10-27,
że pełne formaty wysokiej precyzji w `Image` (wariant B) nie są planowane. Wariant A (dekodery oddają rzędy `Rgba128`)
odpada (D18): dekoder ze źródłem powyżej 8 bitów oddaje `Rgba64`, a `ConvertToSrgb` liczy na nim we float i kwantyzuje
raz; przy `ToSrgb` z żądaniem `Rgba32` serwis dekoduje wewnętrznie z `PreserveBitDepth` i zmniejsza głębię po konwersji.
Pierwotna motywacja (JPEG XL XYB+ICC) jest już załatwiona we wtyczce, która renderuje XYB do sRGB we float.

Decyzje projektowe (2026-10-28, sekcja 11 propozycji):
- D1: niegeneryczny `Image` z `PixelType` (`Rgba32`, `Rgba64`), nie `Image<TPixel>`.
- D2: domyślnie dekodowanie do `Rgba32`; `DecoderOptions.PreserveBitDepth` (`bool`) na żądanie; bez opcji „zawsze
  `Rgba64`” (to samo daje `PreserveBitDepth` + `ConvertPixelType`).
- D3: indeksator `image[x, y]` rzuca `InvalidOperationException` na obrazie 16-bitowym; typowane
  `GetPixel<TPixel>`/`SetPixel<TPixel>`.
- D5: piksel 16-bitowy liczy się jako 2 w `MaxPixels`, bez nowego limitu.
- D6: dithering 16→8 tylko w `ConvertPixelType(PixelType.Rgba32, dither: true)`; kodery zaokrąglają bez opcji.
- D9: bez publicznego `IPixel<TPixel>`; typowane API ma ograniczenie `unmanaged` i sprawdza konkretny obsługiwany
  typ, a kontrakty algorytmów pozostają wewnętrzne.
- D10: dodawanie klatek automatycznie rozszerza 8→16; 16→8 wymaga wcześniejszej jawnej konwersji, również przy
  dodawaniu kopii klatki.
- D11: `MeanNormalized`/`PercentileNormalized` zastępują `Mean`/`Percentile`; nowe nazwy wymuszają świadomą
  migrację do wyników w zakresie 0–1.
- D12: `LevelsOptions` z polami `*Normalized` i `CurvesNormalized` (punkty 0–1) zastępują stare nazwy, z tego samego
  powodu co D11.
- D13: tylko generyczny dostęp do pikseli (`GetRowSpan<TPixel>`, `GetPixelSpan<TPixel>`, `GetPixelMemory<TPixel>`,
  `LoadPixelData<TPixel>`, `CopyPixelDataTo/From<TPixel>`); wersje niegeneryczne usunięte, indeksator zostaje `Rgba32`.
- D14: PNG `Auto` zapisuje głębię 8 dla `Rgba64` z dokładną treścią 8-bitową (próbki będące wielokrotnością 257);
  `Sixteen` wymusza 16.
- D15: `sBIT` tylko jako metadane; nie skaluje próbek i nie wpływa na `PreserveBitDepth`, zapisywany przy zgodnej głębi.
- D16: CLI `convert --depth auto|8|16`, domyślnie `8`.
- D17: przy poślizgu po etapie 1 najwyżej prerelease `0.9.0-preview.N` dla autorów wtyczek; stabilne 0.9.0 tylko
  z kompletem operacji.
- D18: bez publicznego SPI rzędów float (wariant A); float `ConvertToSrgb` na `Rgba64`.
- D19: bez `PixelFormat.Bgra64` i bez `RgbaF16` w adapterze Skia (brak odbiorców, F16 stratne).
- D20: ICO zapisuje obrazy 16-bitowe jako 8-bitowe.
- D21: histogram domyślnie ma 256 koszyków; `HistogramOptions.FullResolution` daje koszyk na każdą wartość próbki.
- D22: `IImageFormatEncoder.SupportsPixelType(PixelType)` z domyślną implementacją zamiast enuma flag.
- D23: `Rgba64` ma ten sam zestaw członków co `Rgba32` (stałe, `PackedValue`, `Deconstruct`, równość, `ToString`),
  bez `ToVector4`, parsowania i hex.
- D24: nowa właściwość `PngEncoderOptions.BitDepth` typu `PngBitDepth { Auto, UpTo8, Sixteen }`.
- Przy rekomendacji, dopóki użytkownik nie zmieni: D4 (nieprzeniesione operacje rzucają `NotSupportedException`
  w trakcie prac), D7 (System.Drawing tylko 32bpp), D8 (miejsce na `Rgba128` w `PixelType`, bez dodawania w 0.9.0).

Etapy (1–5 wychodzą razem jako 0.9.0, bez stabilnego wydania z niepełnym zestawem operacji; wyjątek: prerelease z D17):

| Etap | Zakres | Stan |
| --- | --- | --- |
| 1 | `Rgba64`, `PixelType`, typowany dostęp do pikseli, nowe `PixelFormat` i konwersje; PNG 16-bit odczyt i zapis (z `tRNS` i APNG); geometria; zmniejszanie głębi w pozostałych koderach; `SupportsPixelType` w SPI, blokada wtyczek < 0.9.0; budżet `MaxPixels` | Zrobiony (2026-10-28) |
| 2 | Resize, Affine, rozmycia i wyostrzanie, kompozycja, `DrawImage`, premultiplikacja | Zrobiony (2026-10-28; bez SIMD 16-bit tych operacji; ARM64 zestaw funkcjonalny zaliczony w bramce) |
| 3 | Operacje tonalne i kolorystyczne; `LevelsOptions` (`*Normalized`), `CurvesNormalized` i progi na 0–1, `ImageHistogram` z `FullResolution`, `IFrameProcessor<TPixel>` | Zrobiony (2026-10-28; bez SIMD 16-bit tych operacji; ARM64 zestaw funkcjonalny zaliczony w bramce) |
| 4 | Negatywy, czyszczenie (kurz, szum), ICC i `ToSrgb` we float na `Rgba64` (D18), HDR i gain map z bazą `Rgba64` | Zrobiony (2026-10-28; bez SIMD 16-bit tych operacji; ARM64 zestaw funkcjonalny zaliczony w bramce) |
| 5 | Adaptery (Skia `Rgba16161616`, System.Drawing 32bpp); wydania wtyczek TIFF, HEIF, JPEG XL na 0.9.0 (Formats.Extra poza zakresem: decyzja użytkownika 2026-10-28) | Implementacja i weryfikacja lokalna zrobione (2026-10-28): adaptery, TIFF, HEIF Main 10 i JPEG XL 16-bit; testy, wyrocznie, AOT i pomiary 8-bit OK. Przygotowanie wydań zlecone; TIFF 0.7.4, HEIF 0.7.0, JPEG XL 0.7.8, po rdzeniu |

Stan (2026-10-28): etap 1 zaimplementowany (szczegóły i pozostałości w `progres.md`): `Rgba64`, `PixelType`,
generyczny dostęp do pikseli, nowe `PixelFormat`, `ConvertPixelType` z ditheringiem, zasady D10, `PreserveBitDepth`,
ważony `MaxPixels`, `ReservePixels(…, PixelType, …)` z kontrolą zwróconego obrazu, `SupportsPixelType`, blokada wtyczek
< 0.9.0, PNG 16-bit (odczyt wszystkich typów koloru z `tRNS`, Adam7, APNG, czytnik sekwencyjny; zapis z `PngBitDepth`),
geometria przez wspólny dispatch, zaokrąglanie w pozostałych koderach (w tym ICO). Bramka uzupełniła `sBIT`
(D15), SIMD `TrimBounds` dla `ushort` i ważenie części osadzonych. Dalsza optymalizacja: konwersja wierszami zamiast
kopii całego obrazu przy zapisie przez fallback (obecny fallback jest poprawny i objęty testami).
Etap 2 zaimplementowany (2026-10-28; szczegóły, błędy względem referencji i pomiary w `progres.md`): Resize (wszystkie
filtry, encoded/linear, pad), Rotate/Affine, GaussianBlur, BoxBlur, Sharpen, UnsharpMask, Vignette, DrawImage (wszystkie
tryby, mieszane głębokości), ApplyMask, Opacity, Fill, BackgroundColor na `Rgba64` przez `PixelDispatch`; kolory opcji
`Rgba64`; błąd ≤ 1 kod 16-bit względem niezależnej referencji double; ścieżka 8-bit bajtowo bez zmian i bez regresji
w BenchmarkDotNet. Poza etapem 2: dalsze SIMD 16-bit; funkcjonalna weryfikacja ARM64 wykonana w bramce.
Etap 3 zaimplementowany (2026-10-28; szczegóły w `progres.md`): Brightness, Contrast, Gamma, Exposure, Levels, Curves,
Invert, Grayscale, Sepia, Saturate, Vibrance, WhiteBalance, AutoWhiteBalance, AutoContrast, Equalize, ShadowsHighlights,
Clarity na `Rgba64` przez `PixelDispatch` (tablice 65536 × `ushort` z puli albo float, jedno zaokrąglenie; liczności
w pełnej rozdzielczości); API: `LevelsOptions.*Normalized`, `CurvesNormalized`, `MeanNormalized`/`PercentileNormalized`,
`HistogramOptions.FullResolution`, `ImageHistogram.BinCount`, `IFrameProcessor<TPixel>` z
`ApplyProcessor<TPixel>(IFrameProcessor<TPixel>)`, progi `AutoCropOptions.AlphaThresholdNormalized`/`ToleranceNormalized`
i `UnsharpMask(thresholdNormalized)` (decyzja użytkownika 2026-10-28: sufiks `Normalized` jak D12), `MakeTransparent`,
`WhiteBalance(neutral)` i `AutoCropOptions.Background` jako `Rgba64`; stare nazwy usunięte. Ustawienia 8-bit zapisane jako
v / 255f dają dokładnie dawne wyniki. Błąd ≤ 1 kod 16-bit względem referencji double; ścieżka 8-bit bajtowo bez zmian.
Etap 4 zaimplementowany (2026-10-28; szczegóły, błędy względem referencji i pomiary w `progres.md`): DevelopNegative,
InvertNegative, AnalyzeNegative, FindFilmFrame i obrót kadru, RemoveDust, Denoise, ReduceColorNoise na `Rgba64`
(statystyki filmu w pełnej rozdzielczości, mediany kurzu 16-bit, płaszczyzny float); `ConvertToSrgb` na `Rgba64` we float
z jednym zaokrągleniem, `ToSrgb` bez `PreserveBitDepth` dekoduje głębiej, konwertuje i raz zmniejsza do `Rgba32`
(źródła głębsze opłacane jak 16-bit); `ToHdr` z bazą `Rgba64`, `ToneMappingOptions.PixelType`, `ToGainMapImage` z bazą
16-bit (mapa liczona względem zapisanej bazy), mapy 16-bit z wtyczek (`GainMapImage(size, pixelType)`, opłata zadeklarowanego
typu, kontrola typu po dekodowaniu, geometria i próbkowanie w 16 bitach). D4 spełnione: żadna operacja rdzenia nie odrzuca
obrazu 16-bitowego (test obejmujący wszystkie operacje `ImageProcessingContext`, analizę, HDR i kodery). Błąd ≤ 1 kod
16-bit względem referencji double; ścieżka 8-bit bajtowo bez zmian (FindFilmFrame 8-bit +2–4%, zaakceptowane przez użytkownika jako szum pomiarowy 2026-10-28). Bramka wydania jest zaliczona: przewodnik migracji, nota, CLI `--depth`, AOT/trimming x64,
`sBIT`, waga typu w `DecodeEmbeddedPart` i testy ARM64 są uzupełnione. `VERSION` = 0.9.0.
Etap 5 (2026-10-28; implementacja i weryfikacja lokalna zakończone, szczegóły w `progres.md`): adaptery zrobione (Skia `Rgba16161616` ↔
`Rgba64`/premultiplied, System.Drawing 32bpp). Rdzeń i adaptery: pełny Release 9391 testów, 0 błędów, 8 pominiętych
(GDI+ na Linux). TIFF 0.7.4: 4220/4220 na aktualnej paczce rdzenia; Native AOT
linux-x64: 322 dekodowania bez różnic i pełna rampa 16-bit przez LZW/Deflate. HEIF 0.7.0: odczyt i zapis Main 10,
alfa i mapy 16-bit (Main 10 / Monochrome 10), 795/795 funkcjonalnych testów Release (bez Local/Measurement), heif-dec
oraz HM (dokładne YUV 8/10-bit) przechodzą, 28 plików korpusu × odczyt/zapis 8-bit identyczne z HEAD na 0.8.5.
JPEG XL: `PreserveBitDepth`, typowana rezerwacja, zapis Modular 16-bit bezstratny i VarDCT bez zmniejszania do 8 bit,
alfa bezstratna 16-bit; Release 467/467, JIT i Native AOT rzeczywistej końcowej paczki OK.
Golden JPEG XL: 1139/1142 bez zmian (po normalizacji separatora dziesiętnego), 3 zamierzone różnice `ToSrgb` dla
źródeł 10/12/16-bit przez D18; wejścia 8-bit i bajty kodera/transkodera bez zmian. Pomiary 8-bit: bez regresji poza tolerancją 1% po kontroli JIT/CPU; surowe dane
`docs/measurements/plugins-0.9.0-step5.json`. Numeracja i noty wydań: TIFF 0.7.4, HEIF 0.7.0, JPEG XL 0.7.8; bramka rdzenia zaliczona.
Nie publikowano żadnej paczki. HEVC > 10 bit nadal nieobsługiwane — wcześniejszy zapis „10/12-bit HEIF” był zbyt szeroki.

Bramka techniczna 0.9.0 (2026-10-28): **zaliczona**. Release bez ostrzeżeń; pełny x64 9406 testów (9398 OK,
8 GDI+ pominiętych), bez intrinsics tak samo; Oracle 261/261; consumer rdzenia i adapterów z pełnym trimmingiem
oraz Native AOT linux-x64 OK. ARM64 jak wyżej. Nowa regresja GIF sprawdza rozmiar ramki wystającej poza płótno przed
alokacją indeksów. Test generatora HDR dopuszcza 2 ULP liniowego boostu względem natywnej wyroczni ARM; pozostałe
pola są dokładne, a writer ułamków/payloadu pozostaje bajtowo zgodny dla tych samych parametrów.
Migracja: docs/migration-from-0.8.md; nota: docs/releases/release-0.9.0.md; wyniki:
docs/measurements/release-0.9.0-gate.json. **2026-10-28: użytkownik zlecił przygotowanie wydań i scalenie do main. Publiczna publikacja nie została wykonana.**

DoD:
- Każda operacja i kodek wbudowany działa na obrazach `Rgba64` z 16-bitową precyzją (gradient 65536 poziomów nie
  spada do 256); wynik po zaokrągleniu do 8 bitów zgodny z operacją na oryginale 8-bitowym w opisanej tolerancji.
- 8→16→8 bez zmian; ścieżka 8-bitowa bitowo bez zmian (istniejące testy i `reference.json`), bez regresji czasu
  w celowanych pomiarach.
- Korpus 16-bitowych PNG wszystkich typów koloru, z `tRNS` i APNG; referencje z czytnika 16-bitowego (pypng + numpy lub
  ImageMagick), bo Pillow zaokrągla RGB/RGBA 16-bit do 8 bitów.
- Moduł zbudowany na rdzeniu 0.8.5 odrzucany przez `AddFormat`/`ReplaceFormat`.
- Migawka API zatwierdzona, migracja opisana w `docs/plugin-spi.md` i nowym przewodniku; `verify-package-consumer.sh`
  (trimming, Native AOT) zielone; wtyczki TIFF, HEIF i JPEG XL sprawdzone na lokalnej paczce (Formats.Extra poza zakresem decyzją użytkownika).

## Wtyczka HEIF — osobne repozytorium

`PictureSharp.Heif` (`../PictureSharp.Heif`) ma własny `VERSION` i `plan.md` (zakres, DoD, licencje, patenty); zależy
wyłącznie od paczki NuGet `PictureSharp` i publicznego API. Minimalne wersje rdzenia: 0.6.9 (kontener, dekoder i
enkoder), 0.7.0 (gain map → `ToHdr`, `ReadMetadata`), 0.7.1 (pełne `GainMapInfo.Detected`), 0.7.6 (wybór elementu przez
opcje dekodera), 0.8.0 (zapis gain map `tmap`), 0.8.5 (`ImageInfo.Color`), 0.9.0 (obrazy 16-bitowe).

## Poza planem — kandydaci

Do decyzji użytkownika, bez zobowiązań:
- przetwarzanie HDR w float (piksele `Rgba128` w `Image`; 0.9.0 zostawia na nie miejsce w `PixelType`);
- CICP PQ/HLG;
- AVIF (kontener wspólny z wtyczką HEIF).

## Wspólna bramka jakości każdego wydania

- Build Release bez ostrzeżeń, testy dotkniętych obszarów podczas pracy, pełne testy Release i Oracle przed zamknięciem wydania. Pominięta wyrocznia nie potwierdza zgodności.
- Korpus syntetyczny, round-trip, testy uszkodzonych/obciętych danych i limitów dla nowych ścieżek; stare referencje pozostają zgodne w trybach domyślnych.
- Nowe SIMD: identyczność ze skalarem na x64 i rzeczywistym ARM64, sprawdzenie fallbacków; równoległość: deterministyczność i zwalnianie zasobów po błędzie.
- Pomiary przed/po na stałych wejściach i ustawieniach zgodnie z `docs/performance.md`. Bez regresji poza ustaloną tolerancją i bez nieuzasadnionego wzrostu pamięci.
- Aktualne XML docs, migawka publicznego API, przykłady, notices i pakiet zawierający `LICENSE` oraz `THIRD-PARTY-NOTICES.txt`, bez zależności biblioteki.
- `bash scripts/verify-package-consumer.sh` (pełny trimming i Native AOT linux-x64).
- Po znaczącym kroku wpis w `progres.md`: zmiany, decyzje, wyniki, ograniczenia i następny krok. Plan aktualizujemy po rzeczywistym spełnieniu DoD.

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

## Changelog

Szczegóły w `docs/releases/release-x.y.z.md` i historii Git.

- **0.5.0** — kodeki WebP, PNG/APNG, JPEG, GIF, BMP, PCX; animacje, własny deflate/inflate, SIMD x64/ARM64, CLI.
- **0.5.1** — odczyt i zapis ICO (jako RasterSharp).
- **0.5.2** — zmiana nazwy RasterSharp → PictureSharp.
- **0.5.3** — limity `MaxInputBytes`/`MaxMetadataBytes`/`MaxFrames`, anulowanie pracy CPU.
- **0.5.4** — miniatury JPEG przez pomniejszone IDCT (`TargetSize`), bit-exact z libjpeg-turbo.
- **0.5.5** — warstwy, model domenowy, własność buforów, API C# 14 (łamie zgodność z 0.5.4).
- **0.5.6** — API skupione na `Image`, `ImageConfiguration`, publiczne SPI wtyczek, procesory ramek.
- **0.5.7** — porządki w testach, migracja na TUnit.
- **0.6.0** — sekwencyjny odczyt i zapis animacji GIF/APNG/WebP.
- **0.6.1** — typowany EXIF, usuwanie GPS, edycja metadanych bez rekompresji.
- **0.6.2** — konwersja ICC do sRGB (matrix/TRC), resize i kompozycja w świetle liniowym.
- **0.6.3** — AutoCrop, maski, 12 trybów mieszania, histogram, Levels/Gamma, AutoContrast/Equalize, CLI `trim`.
- **0.6.4** — paczki adapterów `PictureSharp.Drawing`, `PictureSharp.Skia`, `PictureSharp.AspNetCore`.
- **0.6.5** — HDR z JPEG z gain map (Apple, ISO 21496-1): `ToHdr`, `ToneMap`, edycja metadanych z zachowaniem mapy.
- **0.6.7** — SIMD dla HDR (deterministyczne Exp2/Log2) i operacji graficznych.
- **0.6.8** — poprawki po review: AutoCrop animacji, Resize Min, TargetSize z limitem wątków, anulowanie VP8L.
- **0.6.9** — gain map zachowana przy operacjach geometrycznych, zapis JPEG z mapą ISO 21496-1 (`JpegEncoderOptions.GainMap`), `ImageInfo.ContainerItemCount`, CLI `-hdr keep`.
- **0.7.0** — integracja i stabilizacja API: SPI wtyczek dla gain map i metadanych, `EncoderOptions.Configuration`, testy end-to-end, CLI `metadata` i `resize -stream`, przewodnik migracji z 0.5.
- **0.7.1** — `ImageDecodeContext.AttachGainMap(…, detected)`: wtyczki zgłaszają wszystkie wykryte modele gain map.
- **0.7.2** — dokumentacja skrócona o ~30%, stabilny test alokacji. Bez zmian API.
- **0.7.3** — mniej alokacji w deflate/inflate PNG i przy metadanych PNG. Bez zmian API i bajtów wyjściowych.
- **0.7.4** — szybszy JPEG: dekodowanie ~2×, kodowanie ~3× na jednym wątku. Bez zmian API i wyników.
- **0.7.5** — szybszy WebP lossy (kodowanie −28…−32%, dekodowanie −8…−11%) i potok wczytaj → orientacja → sRGB → resize → zapis (−40%); naprawiony niedeterminizm wielowątkowego kodowania lossy.
- **0.7.6** — opcje dekodera per wtyczka: `DecoderOptions.FormatOptions` z rekordami `FormatDecoderOptions`.
- **0.7.7** — korekcja fotograficzna: `Exposure`, `WhiteBalance`, `Vibrance`, `Vignette`, `ShadowsHighlights`, `Clarity`, `Denoise`; wyniki identyczne na każdej architekturze i liczbie wątków.
- **0.7.8** — `ImageDecodeContext.DecodeEmbeddedPart`: paski i kafle w innym formacie z własnym limitem części.
- **0.7.9** — `JpegEncoderOptions.ColorMode` (`Auto`, `Color`, `Grayscale`).
- **0.7.10** — `JpegDecoderOptions.CmykStorage` (`AdobeInverted`, `Plain`) oraz `DecodeEmbeddedPart` z `EmbeddedPartOptions`.
- **0.7.11** — (tylko tag i GitHub Release) negatywy: `AnalyzeNegative`, `DevelopNegative`, `InvertNegative`; `Curves`, `WhiteBalance(Rgba32)`, `AutoWhiteBalance`, `RemoveDust`, `ReduceColorNoise`.
- **0.7.12** — `RemoveDust` zostawia szczegóły większe niż plamka, `DevelopNegative` domyślnie bez usuwania kurzu (`DustSize` = 0); zawiera 0.7.11.
- **0.7.13** — `FindFilmFrame`, `DevelopNegative()` sam znajduje, prostuje i kadruje klatkę, korekcja podświetlenia, ostrzeżenia; szybszy obrót bicubic.
- **0.8.0** — wtyczki zapisują gain map: `image.GetGainMap()`, `GainMapMetadata.ToIso()`, `MultiplicationSpace`; JPEG zapisuje mapy dołączone przez wtyczki.
- **0.8.1** — `hdr.ToGainMapImage()` (`GainMapOptions`) jak `generateGainMap` libultrahdr, zapis HDR do JPEG, publiczny konstruktor `HdrImage`, mapy Apple zapisywane jako ISO.
- **0.8.2** — limit zagnieżdżenia obrazów osadzonych (8 poziomów); `FormatMetadata` w `ImageMetadata`; deklaracja stabilnego SPI do 2.0 (`docs/plugin-spi.md`, `PluginSpi.approved.txt`).
- **0.8.3** — ikona projektu w paczkach NuGet i banner w README.
- **0.8.4** — ikona paczek 128×128 (adaptery z ~490 KB do ~30 KB).
- **0.8.5** — `ImageInfo.Color` (model, kanały, typ próbek, alfa, ICC, CICP) i `Compression`; `HdrInfo` → `GainMapInfo`; usunięte `HasAlpha`, `BitsPerChannel`, `WebPCompression`; blokada wtyczek < 0.8.5.
