# AGENTS.md

Instrukcje dla agentów AI (i ludzi) pracujących nad **WebpSharp**: biblioteką .NET do kodowania i dekodowania WebP (oraz odczytu PNG/JPEG i zapisu PNG), napisaną w 100% w C#.

Pełny plan implementacji z fazami i kryteriami ukończenia (DoD) jest w [plan.md](plan.md). Przed rozpoczęciem pracy przeczytaj go i ustal, w której fazie jesteś.

## Twarde zasady

- **Target:** `net10.0`. Biblioteka ma działać na .NET 10 i nowszych.
- **Zero zależności w `src/WebpSharp`:**
  - Nie dodajemy żadnych `PackageReference`, natywnych bibliotek ani P/Invoke.
  - Dozwolony jest tylko BCL, w tym `System.Drawing.Primitives` (`Size`, `Rectangle`, `Point`, `Color`).
  - **Nie używamy** `System.IO.Compression` (`ZLibStream`/`DeflateStream`), bo wołają natywne zlib. Deflate/inflate mamy własne w `Compression/`.
  - Projekty testów, benchmarków i sampli mogą mieć zależności (xUnit, BenchmarkDotNet).
- **Bez słowa kluczowego `unsafe`:**
  - Używamy `Span<T>`, `ref`, `Unsafe.Add`, `MemoryMarshal`.
  - Wyjątek: przypięcie pamięci dla `BitmapData.Scan0` przez `GCHandle`.
- **Publiczne API tylko w namespace `WebpSharp`.** Kodeki i implementacja są `internal`.
- **Alfa prosta.** Piksele w `Image`/`ImageFrame` to `Rgba32` z alfą niepremultiplikowaną.
- **Wyjątki:**
  - Uszkodzone lub nierozpoznane dane rzucają `ImageFormatException`.
  - Poprawne dane z nieobsługiwaną funkcją rzucają `NotSupportedException`.
  - Dekodery nigdy nie mogą wypuścić `IndexOutOfRangeException` itp. dla złośliwego wejścia.
  - Zawsze sprawdzamy limity (`DecoderOptions.MaxPixels`).

## Portowanie z libwebp

- **Referencja:** libwebp **v1.3.2** w `reference/libwebp/` (tylko do odczytu, poza buildem, w `.gitignore`). To ta sama wersja co systemowe `cwebp`/`dwebp`/`webpmux`, które służą jako wyrocznie w testach. Pobranie:
  ```bash
  git clone --depth 1 --branch v1.3.2 https://chromium.googlesource.com/webm/libwebp reference/libwebp
  ```
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
  - Obliczenia `float` zostają w `float`, z tą samą kolejnością operacji. Od tego zależy bit-exactness enkodera względem `cwebp`.
- **Tabele stałych** zapisujemy jako `static ReadOnlySpan<T> Name => [ ... ];`.
- **Licencja:** kod portowany z libwebp to praca pochodna (BSD-3-Clause). Utrzymuj `THIRD-PARTY-NOTICES.txt`. To samo dotyczy fragmentów z libjpeg-turbo (IJG/BSD) i zlib.

## Styl kodu

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
src/WebpSharp/            biblioteka (Image, fasada WebP, Formats/, Compression/, Processing/, Internal/)
tests/WebpSharp.Tests/    xUnit; assets/: generate-corpus.sh, make_reference.py, reference.json
benchmarks/               BenchmarkDotNet
samples/WebpSharp.Cli/    narzędzie CLI (convert/info/resize)
reference/libwebp/        źródła referencyjne (niecommitowane)
```

## Build i testy

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --filter Category=Oracle   # porównania z cwebp/dwebp/webpmux, pomijane gdy brak narzędzi
```

Zasady testowania:
- **Dekodery:** wynik musi być piksel w piksel zgodny z `reference.json`. Hashe SHA-256 RGBA wylicza `make_reference.py` z `dwebp -pam` i Pillow; uruchamiaj go przez `python3 -I`.
- **Enkoder lossless:** round-trip musi dać identyczne piksele.
- **Enkoder lossy:** celujemy w bajty identyczne z `cwebp` 1.3.2. Kryterium zastępcze to parytet PSNR i rozmiaru (patrz DoD w [plan.md](plan.md)).
- **Pobieranie zewnętrznych korpusów** (`fetch-external.sh`: libwebp-test-data, PngSuite, Kodak) wymaga zgody użytkownika.
- Fazę zamykamy dopiero, gdy jej testy przechodzą i spełnione jest DoD.

## Git

- Commitujemy i pushujemy tylko na wyraźne polecenie użytkownika.
- Nie commitujemy `reference/`, `tests/assets/external/`, `bin/` ani `obj/`.
