# AGENTS.md

Instrukcje dla agentów AI (i ludzi) pracujących nad **RasterSharp**: biblioteką .NET do przetwarzania obrazów oraz odczytu i zapisu WebP, PNG, BMP, JPEG i GIF, napisaną w 100% w C#.

Pełny plan implementacji z fazami i kryteriami ukończenia (DoD) jest w [plan.md](plan.md). Przed rozpoczęciem pracy przeczytaj go i ustal, w której fazie jesteś.

## Twarde zasady

- **Target:** `net10.0`. Biblioteka ma działać na .NET 10 i nowszych.
- **Zero zależności w `src/RasterSharp`:**
  - Nie dodajemy żadnych `PackageReference`, natywnych bibliotek ani P/Invoke.
  - Dozwolony jest tylko BCL, w tym `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`).
  - **Nie używamy** `System.IO.Compression` (`ZLibStream`/`DeflateStream`), bo wołają natywne zlib. Deflate/inflate mamy własne w `Compression/`.
  - Projekty testów, benchmarków i sampli mogą mieć zależności (xUnit, BenchmarkDotNet).
- **Bez słowa kluczowego `unsafe`:**
  - Używamy `Span<T>`, `ref`, `Unsafe.Add`, `MemoryMarshal`.
  - Wyjątek: przypięcie pamięci dla `BitmapData.Scan0` przez `GCHandle`.
- **Publiczne API tylko w namespace `RasterSharp`.** Kodeki i implementacja są `internal`.
- **Alfa prosta.** Piksele w `Image`/`ImageFrame` to `Rgba32` z alfą niepremultiplikowaną.
- **Wyjątki:**
  - Uszkodzone lub nierozpoznane dane rzucają `ImageFormatException`.
  - Poprawne dane z nieobsługiwaną funkcją rzucają `NotSupportedException`.
  - Dekodery nigdy nie mogą wypuścić `IndexOutOfRangeException` itp. dla złośliwego wejścia.
  - Zawsze sprawdzamy limity (`DecoderOptions.MaxPixels`).

## Portowanie z libwebp

- **Referencja dla nowych portów:** libwebp **v1.6.0** w `reference/libwebp-1.6.0/` (tylko do odczytu, poza buildem, w `.gitignore`). Narzędzia wyroczni budujemy oddzielnie:
  ```bash
  ./scripts/build-webp-reference.sh
  ```
  Skrypt pobiera tag v1.6.0, sprawdza commit i buduje `cwebp`/`dwebp`/`webpmux`/`img2webp` (a z nagłówkami giflib, `libgif-dev`, także `gif2webp`/`anim_dump`/`anim_diff`) w `reference/libwebp-1.6.0-build/`. Testy i skrypty korpusu wybierają tę lokalną wersję; `RASTERSHARP_WEBP_TOOLS` pozwala wskazać inny katalog. Systemowe narzędzia 1.3.2 pozostają bez zmian.
  Dekodery faz 1–3 portowano pierwotnie z 1.3.2; ich korpus sprawdzono także z 1.6.0. Stary katalog `reference/libwebp/` zachowujemy do porównań historycznych.
- **Sposób portowania:**
  - Portujemy funkcja po funkcji.
  - Nad każdą funkcją piszemy komentarz źródła, np. `// port: src/dec/vp8_dec.c GetCoeffsFast`.
  - Nazwy w miarę możliwości odpowiadają oryginałom (w PascalCase).
- **Kolejność:**
  - Najpierw wersja skalarna z referencyjnych plików `src/dsp/*.c`.
  - SIMD (`Vector128`/`Vector256`) dodajemy dopiero później.
  - Wynik SIMD musi być identyczny ze skalarnym i mieć na to test.
- **Semantyka C, której trzeba pilnować:**
  - promocja do `int`,
  - `clip_8b` zamiast rzutowania `(uint8_t)`,
  - przesunięcia arytmetyczne,
  - mieszanie `uint`/`int`,
  - przepełnienia.
  - Enkoder VP8L używa kosztów stałoprzecinkowych (23 bity ułamkowe) zgodnie z 1.6.0.
  - Tam, gdzie źródło używa `float`, obliczenia zostają w `float`, z tą samą kolejnością operacji. Od tego zależy bit-exactness enkodera względem `cwebp`.
- **Tabele stałych** zapisujemy jako `static ReadOnlySpan<T> Name => [ ... ];`.
- **Licencja:** projekt jest na licencji MIT. Licencje źródeł portowanych (libwebp BSD-3, libjpeg-turbo IJG/BSD, zlib) wymagają zachowania ich not copyright, dlatego utrzymuj `THIRD-PARTY-NOTICES.txt` (jest pakowany do NuGet razem z `LICENSE`).

## Styl kodu

- Używamy C# 14 (`LangVersion=14.0`) i .NET 10. Styl edytora określa `.editorconfig`.
- Stosujemy SOLID i Clean Code tam, gdzie upraszczają kod: jedna odpowiedzialność typu, małe interfejsy dla rzeczywistych granic i wspólne miejsce konfiguracji. Nie dodajemy interfejsów, fabryk ani warstw bez konkretnej potrzeby.
- Nazwa pliku odpowiada głównemu typowi. Publiczne enumy mają osobne pliki; części typu `partial` nazywamy `Typ.Odpowiedzialność.cs`. Małe, ściśle powiązane typy wewnętrzne portów mogą pozostać razem.
- Pliki grupujemy według funkcji: opcje, enumy i rozszerzenia formatu w `Formats/<Format>/`, kontekst i opcje przetwarzania w `Processing/`, pomocnicze operacje wejścia/wyjścia w `Internal/`. Publiczne typy nadal należą wyłącznie do namespace `RasterSharp`.
- Preferujemy dostępne idiomy C#: pattern matching, wyrażenia kolekcji, rekordy i `with`, `field` w walidowanych właściwościach oraz bloki `extension`. Stosujemy je, gdy poprawiają czytelność i zachowują semantykę, alokacje oraz zgodność portu.
- Nullable włączone, `TreatWarningsAsErrors`. Publiczne API ma komentarze XML (`GenerateDocumentationFile`).
- Gorące pętle:
  - bez alokacji,
  - bufory tymczasowe z `ArrayPool<T>.Shared`,
  - piksele obrazu w `GC.AllocateUninitializedArray`.
- Przy krótkich metodach w gorących ścieżkach stosujemy `[MethodImpl(MethodImplOptions.AggressiveInlining)]`.
- Komentarze piszemy oszczędnie. Wyjaśniają „dlaczego” albo wskazują źródło portu.
- Kod piszemy po angielsku (identyfikatory, komentarze, XML docs). Dokumenty projektowe mogą być po polsku.

## Struktura

```
src/RasterSharp/            biblioteka (Image, konwerter WebP, Formats/, Compression/, Processing/, Internal/)
tests/RasterSharp.Tests/    xUnit; assets/: generate_corpus.py, make_reference.py, reference.json, corpus/, reference/
benchmarks/               BenchmarkDotNet
docs/                     dokumentacja użytkownika (przewodniki; przykłady testuje DocsExamplesTests)
samples/RasterSharp.Cli/    narzędzie CLI (convert/info/resize)
reference/libwebp/        źródła referencyjne (niecommitowane)
```

Nowy format pliku rejestrujemy w jednym miejscu, `Formats/CodecRegistry.cs`:
- detektor sygnatury (`IImageFormatDetector`),
- dekoder (`IImageDecoder`),
- enkoder (`IImageEncoder<TOptions>` z własnym rekordem opcji).
Duże kodeki dzielimy na pliki `partial class` (np. `PngDecoder.Chunks.cs`).
`CodecRegistry` wybiera kodeki i udostępnia listę formatów; `ImageCodec` odpowiada za ich wywołanie, normalizację wyjątków i zastosowanie orientacji EXIF.

## Build i testy

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --filter Category=Oracle   # porównania z cwebp/dwebp/webpmux, pomijane gdy brak narzędzi
```

Zasady testowania:
- **Korpus testowy:** `tests/assets/corpus/` (syntetyczne źródła + warianty z `cwebp`/`img2webp`/`webpmux`/`convert`/Pillow) jest commitowany. Regeneracja:
  ```bash
  python3 -I tests/assets/generate_corpus.py && python3 -I tests/assets/make_reference.py
  ```
- **Zdjęcia w `samples/*.jpg|webp`** to prywatne pliki użytkownika (EXIF z GPS) – są w `.gitignore`, nie commitujemy ich; służą tylko do lokalnych testów (`Category=Local`) i benchmarków.
- **Kwantyzacja GIF:** port z Pillow 10.2.0 (źródła C w `reference/pillow-10.2.0/`, tag `10.2.0`); referencje w `gif_quantize_reference.json` regeneruje `python3 -I tests/assets/gif_reference.py`.
- **Dekodery:** wynik musi być piksel w piksel zgodny z `reference.json`. Hashe SHA-256 RGBA wylicza `make_reference.py` z `dwebp -pam` i Pillow; uruchamiaj go przez `python3 -I`.
- **Enkoder lossless:** round-trip musi dać identyczne piksele.
- **Enkoder lossy:** celujemy w bajty identyczne z `cwebp` 1.6.0. Kryterium zastępcze to parytet PSNR i rozmiaru (patrz DoD w [plan.md](plan.md)).
- **Pobieranie zewnętrznych korpusów** (`fetch-external.sh`: libwebp-test-data, PngSuite, Kodak, BMP Suite) wymaga zgody użytkownika.
- Fazę zamykamy dopiero, gdy jej testy przechodzą i spełnione jest DoD.

## Git

- Commitujemy i pushujemy tylko na wyraźne polecenie użytkownika.
- Wersja biblioteki jest w pliku `VERSION` (SemVer). `Directory.Build.props` wczytuje ją do `Version` wszystkich projektów.
- Nie commitujemy `reference/`, `tests/assets/external/`, `bin/` ani `obj/`.
