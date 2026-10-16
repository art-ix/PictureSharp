# PictureSharp: plan rozwoju do 0.7.0

## Stan i cel

**Bieżąca wersja kodu: 0.6.5 (biblioteka i paczki adapterów; HDR z gain map). Fazy 0–10, ICO, kontrola zasobów, miniatury JPEG, sekwencyjne animacje, metadane, zarządzanie kolorem oraz operacje graficzne ukończone; 0.5.2 zmieniło nazwę biblioteki z RasterSharp na PictureSharp.** Wydano 0.6.5 (HDR z JPEG iPhone); kolejnym etapem jest 0.6.6 (weryfikacja Apple na macOS). Zakres dalszych planowanych wydań nie oznacza istniejącej funkcjonalności.

PictureSharp to biblioteka obrazów dla .NET 10+, napisana w C# 14, bez zależności biblioteki i kodu natywnego. Odczytuje i zapisuje WebP, PNG/APNG, JPEG, GIF, BMP i PCX; wydanie 0.5.1 dodało ICO. Udostępnia animacje, metadane, operacje na pikselach, skalowanie, geometrię, korekcję kolorów i filtry przez `Image.Load` / `Mutate` / `Clone` / `Save` / `SaveAs*`; formaty można rozszerzać wtyczkami.

Do 0.7.0 dodajemy odczyt i zapis ICO oraz rozwijamy sześć obszarów: miniatury przy dekodowaniu, kontrolę zasobów, sekwencyjne animacje, zarządzanie kolorem, metadane i operacje graficzne. W 0.6.5 dodajemy odczyt HDR z JPEG pochodzących z iPhone’a (obraz bazowy i mapa wzmocnienia, gain map). Pozostałe nowe formaty (CUR, TIFF, QOI, TGA, PNM, HEIF/HEIC, AVIF, JPEG XL) pozostają poza tym planem.

Szczegóły istniejącego API i ograniczeń: `README.md`, `docs/`, XML docs oraz `tests/assets/PublicApi.approved.txt`. Wyniki pomiarów: `docs/performance.md`. Lokalna historia pracy: `progres.md` (bez commitowania). Historyczne szczegóły implementacji pozostają w historii Git.

## Zasady obowiązujące we wszystkich wydaniach

- `net10.0`, `LangVersion=14.0`, nullable, warnings as errors i XML docs. Publiczne API w `PictureSharp` (model, opcje, konfiguracja i rozszerzenia `Image`), `PictureSharp.Processing`, `PictureSharp.Formats` (SPI wtyczek) i `PictureSharp.Formats.<Format>`; implementacja jest `internal`.
- Biblioteka używa wyłącznie BCL: bez `PackageReference`, P/Invoke, `unsafe` oraz `System.IO.Compression`. Deflate/inflate pochodzi z `Algorithms/Compression/`. `System.Drawing.Primitives` jest dozwolone.
- `Image` i `ImageFrame` przechowują `Rgba32` z prostą alfą. Bufory robocze z `ArrayPool<T>`, piksele z `GC.AllocateUninitializedArray`; gorące pętle bez alokacji.
- Zachowujemy dotychczasowe wyniki i domyślne zachowanie. Nowe przekształcenia obrazu wymagają jawnej opcji. Ewentualne nowe domyślne limity zasobów muszą być opisane jako zmiana zachowania.
- Uszkodzone dane: `ImageFormatException`; poprawne, nieobsługiwane warianty: `NotSupportedException`. Anulowanie: `OperationCanceledException`. Błędy I/O zachowują właściwy typ. Żadne złośliwe wejście nie może powodować niekontrolowanych wyjątków indeksowania lub przepełnienia.
- Nowe porty WebP bazują na read-only `reference/libwebp-1.6.0/`, z komentarzami źródłowymi i aktualizacją `THIRD-PARTY-NOTICES.txt`. Zachowujemy semantykę C, w tym kolejność operacji `float` i 23-bitowe koszty VP8L.
- Najpierw poprawna wersja skalarna. SIMD x64/ARM64 i równoległość dodajemy po pomiarach; wyniki muszą być identyczne ze skalarem i niezależne od liczby wątków.
- Testy, benchmarki i wyrocznie mogą używać zewnętrznych pakietów. Pobieranie zewnętrznych korpusów wymaga zgody użytkownika. Prywatne zdjęcia pozostają lokalne.
- Commit, push i publikacja wyłącznie na polecenie użytkownika. `VERSION` zmieniamy przy przygotowaniu wydania, nie przy rozpoczęciu etapu.

## Przebudowa architektury — wydanie 0.5.5

Zatwierdzony zakres: jeden projekt i pakiet, czysty model obrazu, wydzielone operacje I/O i przetwarzania, funkcjonalne namespace, migracja API bez fasady zgodności. Szczegóły: [architektura](docs/architecture.md) i [migracja](docs/migration-layered-api.md).

DoD: build Release bez ostrzeżeń; testy regresji i dostępne Oracle; testy zależności IL i braku cykli warstw; nowa migawka API; pomiary istniejącej macierzy przed/po; pakiet NuGet i konsument trimming/Native AOT. Zmiana nie dodaje funkcjonalności kolejnych wydań. Wydanie 0.5.5 obejmuje te zmiany i celowo łamie zgodność API z 0.5.4; migracja jest opisana w przewodniku.

Weryfikacja linux-x64: build bez ostrzeżeń, 5081/5081 Release, 150/150 Oracle, 113/113 testów SIMD bez intrinsics, poprawne pakowanie oraz konsumenci trimming/Native AOT (także z lokalnej paczki). Macierz 80 przypadków: brak potwierdzonej powtarzalnej regresji >5%. ARM64 wymaga osobnego wykonania. [Pełne wyniki](docs/refactoring-verification.md).

## Ukończona baza: 0.5.0

- Kodeki sześciu formatów, animacje WebP/APNG/GIF, własny deflate/inflate, przetwarzanie i CLI.
- Zgodność z korpusem i wyroczniami; WebP dąży do identycznych bajtów z `cwebp` 1.6.0, dekod do identycznych pikseli z `dwebp`. Bezstratne formaty zachowują piksele w obsługiwanym modelu kolorów.
- SIMD x64 i ARM64, pomiary pełnych operacji, redukcja alokacji. Bramka WebP ≤3× natywnego C została spełniona na ustalonej macierzy.
- Testy Release, Oracle, odporność, pakiet NuGet i dokumentacja zweryfikowane. Szczegóły i ograniczenia pomiarów w `docs/performance.md`.

Punkty wyjścia dla nowych prac: wejściowe strumienie są buforowane w całości; wszystkie klatki animacji są przechowywane jako pełne płótna; ICC jest przenoszony bez stosowania; CMYK→RGB jest uproszczone; PNG 16-bit jest redukowane do 8 bitów. `MaxPixels` obejmuje sumę pikseli dekodowanych klatek, a `FirstFrameOnly` liczy jedną.

## Kolejność wydań

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.5.1 | Odczyt i zapis ICO (jako RasterSharp) | Ukończona — opublikowana |
| 0.5.2 | Zmiana nazwy RasterSharp → PictureSharp | Ukończona — opublikowana |
| 0.5.3 | Kontrola zasobów i anulowanie pracy CPU (funkcjonalność 2) | Ukończona — opublikowana |
| 0.5.4 | Miniatury podczas dekodowania JPEG (1) | Ukończona — opublikowana |
| 0.5.5 | Warstwy, model domenowy, własność buforów i API C# 14 | Ukończona — wydanie 0.5.5 |
| 0.5.6 | API skupione na `Image`, `ImageConfiguration`, wtyczki formatów i procesory ramek | Ukończona — opublikowana |
| 0.5.7 | Porządki w testach: duplikaty, mniejsze macierze, kategoria `Slow`, migracja na TUnit | Ukończona — wydanie 0.5.7 |
| 0.6.0 | Sekwencyjny odczyt i zapis animacji (3) | Ukończona — wydanie 0.6.0 |
| 0.6.1 | Typowane metadane i edycja bez rekompresji (5) | Ukończona — wydanie 0.6.1 |
| 0.6.2 | Zarządzanie kolorem i światło liniowe (4) | Ukończona — opublikowana |
| 0.6.3 | AutoCrop, maski, mieszanie i korekcja tonalna (6) | Ukończona — opublikowana |
| 0.6.4 | Adaptery jako osobne paczki: System.Drawing, SkiaSharp, ASP.NET Core | Ukończona — wydanie 0.6.4 |
| 0.6.5 | HDR z JPEG iPhone: gain map Apple i ISO 21496-1, rekonstrukcja HDR i konwersja do SDR | Ukończona — wydanie 0.6.5 (weryfikacja Apple na macOS przeniesiona do 0.6.6) |
| 0.6.6 | Weryfikacja HDR z ImageIO/Core Image na macOS, benchmarki HDR, usuwanie deklaracji Ultra HDR przy zapisie SDR | Planowana |
| 0.7.0 | Integracja ICO, sześciu obszarów, adapterów i JPEG HDR, stabilizacja API i wydanie | Planowana |

Numery określają kolejność, nie terminy. Każde wydanie wymaga własnego DoD oraz wspólnej bramki jakości. Nazwy nowych opcji i metod poniżej są robocze do czasu zatwierdzenia migawki API.

## 0.5.1 — odczyt i zapis ICO

Zakres:
- Zarejestrować ICO w `CodecRegistry`: detektor, dekoder, enkoder, `ImageFormat.Ico`, opcje w `Formats/Ico/` i rozszerzenia `SaveAsIco` / `ToIco`, także zapis async. Obsługa w `Identify`, CLI i dokumentacji.
- Odczytywać kontener z pojedynczymi lub wieloma wariantami: osadzony PNG oraz nieskompresowany DIB z paletą 1/4/8-bit i truecolor 24/32-bit. Wykorzystać istniejące kodeki i wspólne helpery, uwzględniając specyfikę DIB w ICO.
- Poprawnie interpretować maskę AND, kanał alfa 32-bit, padding wierszy i orientację rastera. Jawnie ustalić regułę dla starszych ikon z zerowym kanałem alfa oraz sprzeczności maski i alfy.
- `Image.Load` zwraca jeden wybrany wariant, domyślnie największy obsługiwany, z deterministycznym rozstrzyganiem remisów; umożliwić wybór indeksu lub preferowanego rozmiaru. `Identify` opisuje ten sam wybór. Ikona nie jest animacją.
- Osobne API do listowania wariantów (rozmiar, głębia, sposób kodowania), odczytu wybranego wariantu i zapisu kolekcji obrazów o różnych rozmiarach. Nie wykorzystywać `Image.Frames` jako kolekcji wariantów ICO; przed implementacją ustalić własność i zwalnianie obrazów.
- Zapis pojedynczego obrazu i zestawu wariantów 1–256 px w każdej osi; tryb PNG oraz DIB 32-bit z maską AND. Generowanie typowych rozmiarów ikon przez jawnie wybrany resize. Nie kwantyzować kolorów bez żądania użytkownika.
- Sprawdzać katalog, offsety, długości, wymiary i rozmiary alokacji przed dekodem, także wewnątrz osadzonego PNG/DIB. `MaxPixels` obejmuje wybrany obraz, a przy ładowaniu kolekcji sumę ładowanych wariantów. Uszkodzone dane nie mogą być po cichu pomijane jako nieobsługiwane.
- CUR, animowane kursory ANI, zasoby ikon w PE oraz pozostałe warianty DIB poza zakresem tego wydania; poprawne nieobsługiwane warianty zgłaszać zgodnie z zasadami biblioteki.

DoD:
- Syntetyczny korpus: PNG/DIB, wszystkie obsługiwane głębie, maski i alfa, nieparzyste szerokości, pojedyncze i mieszane warianty, rozmiar 256 px. Referencje RGBA porównane z niezależnym narzędziem, np. Pillow/ImageMagick.
- Round-trip PNG i DIB 32-bit zachowuje piksele w udokumentowanej semantyce przezroczystości; zapisane pliki i wszystkie warianty poprawnie odczytywane przez wyrocznię.
- Testy wyboru wariantu, identyfikacji, kolekcji różnych rozmiarów, limitów, obcięć, błędnych offsetów i rozbieżności między katalogiem a osadzonym obrazem.
- Migawka API, przykłady tworzenia favicony/wielowariantowej ikony i konwersji ICO w CLI; wspólna bramka jakości zaliczona.

Weryfikacja 2026-10-15: build Release bez ostrzeżeń; 4136/4136 testów Release,
150/150 Oracle, 19/19 testów ICO i jego przykładów bez intrinsics. Korpus ma
14 plików ICO, referencje każdego wariantu z Pillow (dla paletowego PNG z tRNS
bezpośrednio z osadzonego PNG, dodatkowo wyrocznia ImageMagick). CLI i pakiet
z lokalnego feedu sprawdzone; pomiary w `docs/performance.md`. Nie dodano nowych
ścieżek SIMD. API i ograniczenia: `docs/ico.md`. VERSION 0.5.1; README i plan zaktualizowane do wydania. Opublikowane
jako RasterSharp 0.5.1 (tag v0.5.1, GitHub Release z paczką).

## 0.5.2 — zmiana nazwy na PictureSharp

Nazwa RasterSharp jest zajęta. Wydanie zmienia wyłącznie nazwę: pakiet NuGet, assembly, przestrzeń nazw `PictureSharp`, projekty, CLI `picturesharp-cli`, zmienne środowiskowe `PICTURESHARP_*`, dokumentację i repozytorium `art-ix/PictureSharp`. Kod, formaty i wyniki bez zmian względem 0.5.1.

DoD: brak pozostałości starej nazwy w śledzonych plikach poza historią; build Release bez ostrzeżeń, pełne testy Release i Oracle, paczka `PictureSharp.0.5.2.nupkg` z `LICENSE` i notices, tag v0.5.2 i GitHub Release.

## 0.5.3 — kontrola zasobów i anulowanie

Zakres:
- Dodać limity `MaxInputBytes`, `MaxMetadataBytes` (łączny rozmiar obsługiwanych metadanych, także po dekompresji) i `MaxFrames`; zachować dotychczasową semantykę `MaxPixels`.
- Sprawdzać limity przed alokacją lub powiększeniem bufora. Dla strumieni bez `Seek` egzekwować limit w trakcie odczytu; nie polegać wyłącznie na deklarowanej długości.
- Limity stosować konsekwentnie w `Load`, wariantach async i odpowiednich ścieżkach `Identify`; `IgnoreMetadata` nie może pozwalać na nieograniczoną dekompresję ignorowanych danych.
- Przekazywać `CancellationToken` do pracy CPU kodeków i przetwarzania, także do operacji synchronicznych przez spójne przeciążenia. Kontrole w ograniczonych porcjach pracy, bez kosztu sprawdzania każdego piksela.
- Ustalić i opisać wartości domyślne, zakres limitów oraz zachowanie po błędzie. Nie obiecywać twardego limitu całej pamięci procesu na podstawie samego `MaxPixels`.

DoD:
- Testy granic każdego limitu, strumieni seekable/non-seekable, zaniżonych deklaracji długości, dużych metadanych i animacji dla wszystkich właściwych formatów.
- Anulowanie przed startem i w trakcie odczytu, dekodu, kodowania oraz przetwarzania kończy operację i zwraca bufory. Udokumentowane zachowanie częściowego wyniku zapisu i obrazu po przerwanym `Mutate`.
- Pomiary narzutu kontroli i szczytowej pamięci; brak nieograniczonego buforowania wejścia ponad ustawiony limit.

Weryfikacja 2026-10-15: `MaxInputBytes` (domyślnie `Array.MaxLength`), `MaxMetadataBytes` (64 MiB łącznie, po
dekompresji) i `MaxFrames` (65536) w `Load`/`LoadAsync`/`Identify`/`IcoFile`; token w pracy CPU wszystkich kodeków i
przetwarzania, przeciążenia synchroniczne z tokenem. Zmiany zachowania (limit metadanych i klatek, pierwszy `iCCP`/`eXIf`
w PNG) opisane w `docs/limits-and-cancellation.md`. Build Release bez ostrzeżeń, 4823/4823 testów Release (dwa
przebiegi), 150/150 Oracle; migawka API (wyłącznie dodane elementy) zatwierdzona. Narzut kontroli w granicach szumu,
alokacje bez zmian, odczyt strumienia alokuje 1× wejście (wcześniej 2–3,2×); szczegóły w `docs/performance.md`. Nowych
ścieżek SIMD brak. VERSION 0.5.3; README zaktualizowane do wydania.

## 0.5.4 — miniatury bez pełnego dekodowania JPEG

Zakres:
- Opcja docelowego rozmiaru przy ładowaniu: zachowanie proporcji, bez powiększania domyślnie, jednoznaczna kolejność względem EXIF orientation.
- Pomniejszone IDCT JPEG (1/2, 1/4, 1/8) z doborem skali zapewniającej wystarczający raster do końcowego resize; obsługa baseline i progressive.
- Nie tworzyć pełnowymiarowego bufora RGBA dla miniatury. Osobno mierzyć pamięć współczynników potrzebną JPEG progressive.
- Dla pozostałych formatów jawnie udokumentowany pełny dekod + resize; nie deklarować tam oszczędności dekodera.
- Limity nadal sprawdzają źródłowe wymiary i wymagane bufory; mały rozmiar docelowy nie omija ochrony przed złośliwym wejściem.

DoD:
- Porównanie pomniejszonego dekodu z libjpeg-turbo przy zgodnych ustawieniach; ustalona i zapisana tolerancja pikseli. Nie wymagać identyczności z pełnym dekodem + resize, bo to inny algorytm.
- Testy nieparzystych rozmiarów, skali, subsamplingu, szarości, CMYK, progressive i orientacji EXIF 1–8.
- Benchmarki zdjęć 12 MP i większych do miniatur 256/512/1920 px: czas, alokacje i szczytowa pamięć wobec `Load` + `Thumbnail`. Wykazana redukcja pamięci baseline; ograniczenia progressive opisane.

Weryfikacja 2026-10-15: `DecoderOptions.TargetSize`/`TargetResampling`/`TargetReducingGap` (domyślnie 2, jak Pillow
`thumbnail` i libvips); resize przed orientacją, ramka liczona dla zwracanej orientacji, rozmiar zawsze jak `Thumbnail`.
Pomniejszone IDCT 4×4/2×2/1×1 z `jidctred.c`, skalowanie chromy w IDCT i wybór upsamplingu jak libjpeg-turbo
(`jdmaster.c`, `jdsample.c`). Tolerancja 0: bit-exact z libjpeg-turbo 2.1.5 (Pillow 10.2.0 `draft`) dla 46 plików
korpusu × 1/2, 1/4, 1/8 (`jpeg_scaled_reference.json`), uszkodzonych strumieni i lokalnych zdjęć 12/27 MP, baseline
i progressive. Testy EXIF 1–8, subsamplingu, szarości, CMYK/YCCK, nieparzystych rozmiarów, limitów i alokacji. Pozostałe
formaty: pełny dekod + `Thumbnail`, identycznie z ręcznym wywołaniem. Pomiary w `docs/performance.md`: szczyt pamięci
baseline 12 MP→256 px 68→7 MiB, 27 MP 149→4 MiB; progressive ograniczony przez współczynniki (2 B/próbkę). Build Release
bez ostrzeżeń, 5076/5076 testów, 150/150 Oracle; migawka API (3 dodane właściwości). Bez nowych ścieżek SIMD.
VERSION 0.5.4; paczka z symbolami `.snupkg` i Source Link. README zaktualizowane do wydania.

## 0.5.6 — API skupione na `Image` i rozszerzalność

Biblioteka nie ma jeszcze zewnętrznych użytkowników, więc zmiana łamie API 0.5.5 bez przewodnika migracji. Model DDD pozostaje bez zmian.

- `Image` jako jedyne wejście (styl ImageSharp): `Image.Load/LoadAsync/Identify/IdentifyAsync/DetectFormat`, `image.Save/SaveAsync/Encode/Mutate/Clone`, `ImageFormat.All/FromExtension/FromMimeType`, `format.CanDecode/CanEncode` jako extension blocks C# 14 w `PublicApi`, namespace `PictureSharp`. Usunięte `ImageReader`, `ImageWriter`, `ImageFormats` i namespace `PictureSharp.IO`.
- Niezmienna `ImageConfiguration` z builderem (`KeepOnly`, `RemoveFormat`, `AddFormat`, `ReplaceFormat`), podmienialne `ImageConfiguration.Default` i `DecoderOptions.Configuration`.
- Publiczne SPI wtyczek (`IImageFormatModule`, `IImageFormatDecoder`, `IImageFormatEncoder<TOptions>`, `ImageDecodeContext`), `ImageFormat.Create`, otwarty konstruktor `EncoderOptions`; adaptery egzekwują limity i bezpieczną detekcję.
- `IFrameProcessor` i `ApplyProcessor<T>` z atomową podmianą ramek.
- Paczka dodaje `global using PictureSharp;` przy `ImplicitUsings` (opt-out `PictureSharpImplicitUsings=false`, wyłączone dla Windows Forms, WPF i MAUI).

DoD: Release bez ostrzeżeń; pełne testy, Oracle i testy architektury; testy wtyczek, konfiguracji i procesorów; migawka API przejrzana; konsument C# 14 z jednym `using`; `verify-package-consumer.sh` z trimmingiem, Native AOT, wtyczką, procesorem i global usings; dokumentacja z `Image.*` jako formą domyślną.

## 0.5.7 — porządki w testach

Biblioteka bez zmian; zmienia się tylko projekt testów. Usunięte testy pokryte przez inne (losowa korupcja per kodek, geometria, formaty pikseli, część testów klonowania i resize), macierze niezależne od formatu (anulowanie, `MaxInputBytes`, kwantyzacja przez enkoder GIF) zawężone do reprezentatywnych przypadków, powiązane testy połączone, a fuzzing i testy wielowątkowe oznaczone `Category=Slow` (CI uruchamia je dalej). Testy przeniesione z xUnit na TUnit (Microsoft.Testing.Platform, `--treenode-filter`) z zachowaniem semantyki asercji xUnit; testy Oracle bez narzędzi są raportowane jako pominięte.

DoD: Release bez ostrzeżeń; 3854/3854 testów (wcześniej 4610), 149/149 Oracle, 113/113 bez intrinsics; każdy usunięty przypadek ma wskazane pokrycie; po migracji identyczna liczba przypadków każdej metody.

## 0.6.0 — sekwencyjne animacje

Zakres:
- Osobny czytnik i zapis klatek WebP/APNG/GIF, bez zmiany znaczenia istniejącego `Image.Frames`.
- Odczyt kolejnych skomponowanych klatek z czasem, pętlą i tłem. Jawna własność bufora: kiedy jest ważny, kiedy trzeba go skopiować i kto go zwalnia.
- Kodowanie z sekwencji klatek przy ograniczonym buforze bieżących/poprzednich płócien. Zachować blend/dispose, przezroczystość i czasy.
- Wspierać wejścia i wyjścia bez `Seek`. Dla nagłówków wymagających długości lub liczby klatek zastosować jawny kontrakt (np. znana liczba klatek, seek albo kontrolowany plik tymczasowy), bez ukrytego buforowania całej animacji w RAM.
- Udokumentować różnice optymalizacji względem enkodera mającego wszystkie klatki, np. paletę GIF i wybór klatek kluczowych WebP.
- Obsłużyć anulowanie, przedwczesne zakończenie enumeracji, błędy I/O i sprzątanie zasobów. Egzekwować limity wejścia i łączne limity klatek/pikseli również przy odczycie sekwencyjnym.

DoD:
- Wynik czytnika piksel w piksel oraz czasy/pętla zgodne z istniejącym dekoderem; nowe pliki sprawdzone zewnętrznymi wyroczniami.
- Konwersje GIF↔WebP↔APNG bez przechowywania całej animacji w pamięci.
- Pomiary dla 10/100/1000 klatek o stałym płótnie pokazują brak wzrostu pamięci proporcjonalnego do liczby pełnych klatek; niewielkie indeksy i koszty formatu raportowane osobno.
- Testy blend/dispose, częściowych prostokątów, pustej/obciętej sekwencji, niedotrzymanej deklaracji liczby klatek, non-seekable i anulowania.

Weryfikacja 2026-10-15: `Image.OpenFrameReaderAsync` i `Image.CreateFrameWriter`, publiczne reader/writer/options,
GIF/APNG/WebP z wejścia non-seekable, zapis forward-only GIF i APNG ze znaną liczbą klatek; pozostałe wyjścia przez
jawny `UseTemporaryFile`. `FrameCount=1` używa zwykłego enkodera. Własność borrowed buffers, metadane/pętla znane
po końcu, limity, anulowanie, cleanup i różnice optymalizacji opisane w `docs/animation.md`.

Czytniki zgodne piksel w piksel, czasy/pętla/tło/metadane/DPI dla 206 plików korpusu (plik i non-seekable).
Nowe pliki zweryfikowane przez Pillow i libwebp; 9 par konwersji, blend/dispose i częściowe prostokąty z korpusu,
puste/obcięte sekwencje, count mismatch, błędy I/O i anulowanie (także kopiowania temp). APNG: fallback statyczny tylko
przed pierwszą zwróconą klatką, różna liczba klatek od acTL odrzucana. GIF ma paletę i kwantyzację per klatka oraz pełne
prostokąty; APNG Auto=RGBA, bez indexed; WebP współdzieli okno klatek kluczowych z zapisem wsadowym.

Build Release 0 ostrzeżeń/błędów; **4651/4651 pełnych testów**, **152/152 Oracle** bez pominięć, **113/113 fallback SIMD**;
paczka z notices/LICENSE, konsument z trimmingiem i Native AOT linux-x64 oraz przykładami sekwencji poprawny. API snapshot
przejrzany; README quick start kompiluje się z jednym using. Pomiary 10/100/1000 klatek i raport ograniczonej pamięci:
`docs/performance.md`, `docs/measurements/sequence-memory.tsv`. Macierz 80 przypadków przed/po (2 przebiegi na wersję, ABBA): mediana
+0,09%, alokacje maks. +0,49%; 2 podejrzane przypadki sprawdzone naprzemiennie (+1,62% / −0,91%), brak potwierdzonej
regresji >5%; `docs/measurements/sequences-benchmarks.csv`. Nowych SIMD nie dodano. Rzeczywisty ARM64 i zdalny CI nieuruchamiane.
VERSION 0.6.0; README, instrukcja instalacji i PackageReleaseNotes zaktualizowane. Notatki: `docs/releases/release-0.6.0.md`.
Publikacja przez workflow Release, po bramce Validate (managed, Oracle i package-consumer).

## 0.6.1 — metadane

Zakres:
- Typowany odczyt EXIF: orientacja, DPI, daty, producent/model aparatu, ekspozycja, przysłona, ISO, ogniskowa i GPS. Zachować dostęp do surowych bloków ICC/EXIF/XMP.
- Polityka zachowania wybranych pól oraz usuwanie GPS. Rozróżnić usunięcie znanych tagów GPS od pełnego usunięcia metadanych; nie obiecywać anonimizacji nieznanych MakerNotes/XMP.
- Edycja EXIF/XMP/ICC w JPEG, PNG/APNG i WebP bez dekodowania/rekompresji pikseli; zakres tagów zapisywalnych określony jawnie.
- Poprawne długości, offsety, CRC i nagłówki kontenerów; pozostawienie nienaruszonych danych obrazu, animacji i bezpiecznych nieznanych segmentów/chunków.
- Zapis do osobnego wyniku; atomowe zastąpienie pliku dopiero po poprawnym zakończeniu. Limity i anulowanie z 0.5.3.

DoD:
- Testy EXIF little-/big-endian, niepoprawnych offsetów, cyklicznych IFD, brakujących pól i dużych bloków.
- Dla edycji bez rekompresji identyczne payloady danych skompresowanego obrazu i identyczne piksele/klatki; poprawne metadane potwierdzone niezależnym narzędziem.
- Usuwanie GPS sprawdzone na syntetycznych fixture'ach; oryginalny plik pozostaje poprawny po anulowaniu lub błędzie zapisu.

Weryfikacja 2026-10-15: `Image.ReadMetadata` / `EditMetadata` (ścieżki, strumienie, pamięć, async I/O),
`ImageMetadata.ExifProfile`, typowane `ExifTag<T>` i dokładne `ExifRational`. Odczyt/zapis obsługiwanych tagów,
allowlista i fizyczne usuwanie znanych danych GPS; nieznane dane i offsety EXIF zachowane, nieudane edycje atomowe.
JPEG (także progressive), PNG/APNG i WebP przepisywane bez dekodowania/rekompresji obrazu; testy porównują
skompresowane payloady oraz piksele i klatki. JPEG MPF/MPO i extended XMP jawnie nieobsługiwane. Pliki zastępowane
atomowo po sukcesie, wejścia non-seekable, limity i anulowanie zweryfikowane. Szczegóły API: `docs/metadata.md`.

Build Release bez ostrzeżeń; 4708/4708 pełnych testów (0 pominiętych), w tym 155 Oracle; 113/113 SIMD bez intrinsics.
Migawka API zawiera wyłącznie dodatki, konsument C# 14, trimming i Native AOT linux-x64 poprawne. Macierz 80 przypadków
względem 0.6.0: mediana czasu +0,74%, maks. wzrost alokacji +0,66%; pięć podejrzanych wyników potwierdzonych
naprzemiennie w jednym procesie, żadnej powtarzalnej regresji >5%. Pomiary nowego API i dane surowe w
`docs/performance.md`. VERSION 0.6.1, notatki `docs/releases/release-0.6.1.md`; przygotowany lokalny commit na polecenie użytkownika, bez pusha i publikacji. ARM64 wymaga osobnego wykonania;
nie dodano nowych ścieżek SIMD.

Poprawki API sekwencyjnego z 0.6.0 (addytywne, w 0.6.1): `CreateFrameWriter(path, size[, options])` z formatem
z rozszerzenia, `ImageFrameWriter.Width/Height`, `WriteAsync(frame, duration)` i `WriteAsync(image, duration)`,
`ImageFrameReader.CloneCurrent()` (strumieniowe przetwarzanie klatek przez `Mutate`) oraz
`ImageFrameWriterOptions.From(reader)`. Bajty wyjścia kodeków bez zmian. Weryfikacja 2026-10-15: build Release bez
ostrzeżeń, 4736/4736 testów, 155/155 Oracle, package consumer (trim + Native AOT linux-x64) OK.

## 0.6.2 — zarządzanie kolorem i światło liniowe

Zakres:
- Jawna konwersja do sRGB dla ograniczonego podzbioru profili ICC v2/v4: RGB matrix/TRC oraz Gray/TRC. Obsłużyć wymagane krzywe, macierze i adaptację punktu bieli; opisać obsługiwany rendering intent.
- Profile LUT, pełne CMYK ICC i pozostałe warianty poza zakresem 0.7.0. Przy żądaniu konwersji nieobsługiwany profil daje `NotSupportedException`, uszkodzony `ImageFormatException`; domyślne przenoszenie profilu pozostaje możliwe.
- Jednoznaczna polityka dla braku profilu i konfliktów informacji o kolorze (ICC, sRGB, gAMA/cHRM PNG); brak cichej podwójnej konwersji.
- Po konwersji aktualizować oznaczenie przestrzeni i metadane, aby wynik nie zachował błędnego profilu źródłowego.
- Opcjonalny resize i kompozycja w świetle liniowym sRGB, z poprawną premultiplikacją alfy w buforach roboczych i prostą alfą w `Rgba32`.
- Domyślny resize/kompozycja zachowują obecne wyniki. Nie rozszerzamy magazynu pikseli do HDR/16-bit; bufory obliczeniowe mogą mieć większą precyzję.

DoD:
- Wektory i obrazy kontrolne porównane z niezależnym silnikiem kolorów (np. LittleCMS tylko jako wyrocznia); przed implementacją zapisana tolerancja błędu i konfiguracja referencji.
- Testy sRGB, profili szerokogamutowych matrix/TRC, szarości, błędnych/nieobsługiwanych profili, krzywych i punktu bieli.
- Testy krawędzi z alfą, gradientów i mieszania potwierdzają obliczenia liniowe; tryb domyślny nadal zgodny z dotychczasowymi referencjami.

Referencja i tolerancja (ustalone 2026-10-15 przed implementacją; `tests/assets/color_reference.py`,
`tests/assets/corpus/color/color_reference.json`):
- Wyrocznia: LittleCMS 2.14 przez Pillow 10.2.0 `ImageCms`, intent relative colorimetric, bez BPC, flagi 0, cel
  wbudowany sRGB LittleCMS (D65 → D50 Bradford), tryby 8-bit RGB→RGB i L→RGB. Profile testowe są syntetyczne
  (v2/v4, `curv` gamma/tabela/tożsamość, `para` typy 0–4, P3, Adobe RGB, ProPhoto, Rec.2020, sRGB, Gray).
- Tolerancja: RGB matrix/TRC |różnica| ≤ 1 na kanał 8-bit, Gray/TRC ≤ 2, odsetek różniących się kanałów ≤ 7%.
  Kalibracja LittleCMS względem dokładnego modelu double: RGB maks. 1 (≤ 2,2% kanałów), Gray maks. 2 przy czerni
  (16-bitowa ścieżka z odwróconą krzywą sRGB).
- Polityka PNG: iCCP > sRGB > cHRM/gAMA (jak libpng i przeglądarki); same gAMA = prymarne sRGB z krzywą potęgową,
  same cHRM = krzywa sRGB. Chunki koloru po pierwszym IDAT i gAMA = 0 są ignorowane. Brak opisu koloru = sRGB.
  Po konwersji profil i opis koloru są usuwane (sRGB domyślne), więc nie ma podwójnej konwersji.
- Światło liniowe: referencja to Pillow `F` (resampling 32bpc) na liniowych, premultiplikowanych kanałach oraz
  model source-over w double; tolerancja ≤ 1 na kanał.

Weryfikacja 2026-10-15: `ConvertToSrgb()`, `DecoderOptions.ColorConversion = ToSrgb` (z `IgnoreMetadata` →
`ArgumentException`, frame readers → `NotSupportedException`), parser ICC v2/v4 matrix/TRC (RGB, Gray; `curv`,
`para` 0–4), nieobsługiwane → `NotSupportedException`, uszkodzone → `ImageFormatException` (fuzzing 2000 przypadków).
`ImageMetadata.Gamma/Chromaticities/SrgbRenderingIntent` z PNG gAMA/cHRM/sRGB (oba dekodery, enkoder, EditMetadata;
JPEG/WebP odrzucają zmianę). `ResizeOptions.LinearLight`, `DrawImageOptions { Opacity, LinearLight }`; tryb
domyślny bez zmian (referencje Pillow zielone). Wszystkie 13 profili w tolerancji względem LittleCMS, 7 przypadków
PNG, 9 przypadków światła liniowego ≤ 1. Build Release bez ostrzeżeń, 4970/4970 testów, 155/155 Oracle, 113/113
scalar, 56/56 testów koloru bez intrinsics, package consumer (trim + AOT) OK. Pomiar lokalny: resize liniowy ≈ 1,5×
czasu domyślnego, `ConvertToSrgb` 3000×2250 ≈ 38 ms. Bez nowych ścieżek SIMD; macierz regresji wydajności i ARM64
nie były uruchamiane.

Przygotowanie wydania (2026-10-15): review kodu (10 ustaleń, wszystkie poprawione): IgnoreMetadata pomija też
gAMA/cHRM/sRGB, `ConvertToSrgb` atomowe (ReplaceFrames), `IcoFile.LoadAll` obsługuje `ColorConversion`, rozmiar
profilu większy od danych przycinany jak w LittleCMS, zakres `Gamma` jak gAMA, zdegenerowane `CieChromaticities`
odrzucane w konstruktorze, wspólny `PngColorDescription`, wagi resamplingu bez dodatkowych alokacji, `DrawImage`
liniowe bez zbędnych konwersji. VERSION 0.6.2, `docs/releases/release-0.6.2.md`, PackageReleaseNotes. Build Release
bez ostrzeżeń, 4974/4974, Oracle 155/155, scalar 113/113, kolor bez intrinsics 60/60, package consumer 0.6.2 (trim +
AOT, AotSmoke z nowym API). Macierz 80 przypadków względem v0.6.1: mediana +0,28%, alokacje maks. +0,27%; dziewięć
przypadków >5% potwierdzonych naprzemiennie w osobnych ALC: od −1,79% do +2,28%, bez regresji >5%. ARM64 osobno.

## 0.6.3 — operacje graficzne

Zakres porównany z libvips (`find_trim`), Pillow (`getbbox`, `ImageChops`, `ImageOps`), ImageMagick (`-trim -fuzz`,
`-level`, `-compose`), ImageSharp (tryby mieszania, `HistogramEqualization`) i W3C Compositing and Blending Level 1.
Rozszerzenia zatwierdzone 2026-10-15: dodatkowe tryby W3C, AutoCrop po kolorze tła, `FindTrimBounds`, `AutoContrast`,
`Equalize` i komenda CLI `trim`. Nazwy API są robocze do czasu zatwierdzenia migawki.

Zakres:
- `AutoCrop(AutoCropOptions)`: tryb `Alpha` (próg alfy) albo `Background` (kolor tła, domyślnie piksel narożny, z
  tolerancją jak `-trim -fuzz`, ale jako największa różnica kanału RGBA; piksele z alfą 0 pasują do przezroczystego
  tła niezależnie od koloru), opcjonalny padding ograniczony do płótna. Obraz bez treści pozostaje bez zmian (bez
  obrazu 0×0). Animacje: suma obwiedni wszystkich klatek i jedno przycięcie wspólnego płótna; czasy i metadane bez zmian.
  `Image.FindTrimBounds(options) → Rectangle?` zwraca obwiednię bez modyfikacji obrazu.
- Maski `ApplyMask(Image mask, MaskOptions)`: kanał `Alpha` albo `Luminance` (BT.601, jak `Grayscale`), pozycja lub
  kotwica, `Invert`, obszar poza maską `Keep` albo `Transparent`; `A' = A·m/255` z zaokrągleniem, RGB bez zmian. Maska
  może być obrazem docelowym.
- `BlendMode` w `DrawImageOptions`: `Normal`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Difference` oraz
  `HardLight`, `SoftLight`, `ColorDodge`, `ColorBurn`, `Exclusion`. Wzory B(Cb, Cs) i wariant z alfą według W3C
  (`Cs' = (1−αb)·Cs + αb·B(Cb, Cs)`, potem source-over) z opacity; z `LinearLight` w liniowym sRGB. `Normal` używa
  dotychczasowej ścieżki, więc wynik jest bajtowo identyczny z 0.6.2.
- Histogram `Image.GetHistogram(HistogramOptions) → ImageHistogram`: R, G, B, A i luminancja po 256 koszyków, liczba
  pikseli, średnia i percentyl (nearest rank). Przezroczystość: `Include` (domyślnie, jak Pillow `histogram()`) albo
  `Exclude` dla A = 0; opcjonalny region i wybór klatki (pierwsza lub wszystkie). Analiza nie modyfikuje obrazu.
  `WeightByAlpha` usunięte z zakresu (2026-10-15): wymagałoby ułamkowych liczników zamiast całkowitych.
  `AutoContrast`/`Equalize` liczą histogram z wszystkich pikseli i klatek; animacja dostaje jedno wspólne mapowanie.
- `Gamma` i `Levels(LevelsOptions)` dla RGB albo pojedynczego kanału:
  `out = ob + (ow−ob)·clamp((x−ib)/(iw−ib), 0, 1)^(1/γ)` w zakodowanym sRGB. LUT jest liczony w double i zaokrąglany
  połówkowo w górę. Kolejność: poziomy wejściowe, gamma, poziomy wyjściowe. Walidacja `ib < iw`, skończonego γ > 0 i
  zakresu 0–255; alfa bez zmian.
- `AutoContrast(cutoffLow, cutoffHigh, preserveTone)` i `Equalize()` jako port Pillow 10.2.0 `ImageOps`; wspólny
  wewnętrzny LUT dla operacji tonalnych.
- CLI: komenda `trim` (tryb alfa/tło, tolerancja, padding).
- Operacje dostępne przez `Mutate`/`Clone`, analiza jako API odczytowe. Poza zakresem: tryby nierozdzielne
  (Hue/Saturation/Color/Luminosity), pełne operatory Porter-Duff, krzywe ze splajnami, `EntropyCrop`, tekst i grafika wektorowa.

Referencje (przed implementacją, `tests/assets/graphics_reference.py`, `python3 -I`):
- Model double (numpy) dla mieszania, masek i poziomów z alfą. Pillow 10.2.0 `ImageChops` dla nieprzezroczystych
  trybów, `ImageOps.autocontrast/equalize` i `getbbox`. ImageMagick 6 `-trim -fuzz` i `-level` jako dodatkowa wyrocznia.
- Tolerancja: LUT, maski i AutoCrop 0; tryby mieszania ≤ 1 na kanał 8-bit względem modelu double; autocontrast i
  equalize bit-exact z Pillow.

Kolejność prac: referencje → LUT, Gamma i Levels → histogram, AutoContrast i Equalize → AutoCrop i FindTrimBounds →
maski → tryby mieszania (`Compositor.Blend.cs`) → CLI, dokumentacja, migawka API i pomiary.

DoD:
- Małe ręcznie policzone wektory dla każdego trybu, masek, histogramu, gammy i poziomów; testy alfy 0/255 i pośredniej, przycinania oraz granic parametrów.
- Testy animacji, `Clone`/`Mutate`, źródła będącego celem i braku niezamierzonych zmian RGB pod alfą.
- Wyrocznie zielone w ustalonej tolerancji; `DrawImage` z `Normal` bajtowo identyczne z 0.6.2.
- Pomiary pełnych operacji na małych grafikach i 12 MP, macierz regresji względem v0.6.2; SIMD dopiero przy wykazanej korzyści i identyczności ze skalarem.
- Dokumentacja (`docs/image-processing.md`, `docs/cli.md`) z przykładami w `DocsExamplesTests`, migawka API z samymi dodatkami, konsument trim/AOT.

Weryfikacja 2026-10-15: `Levels`/`Gamma`, `AutoContrast`/`Equalize` (bit-exact z Pillow 10.2.0 `ImageOps`),
`GetHistogram`, `AutoCrop`/`FindTrimBounds`, `ApplyMask` i 12 trybów `BlendMode` w `DrawImageOptions`; komenda CLI
`trim`. 59 plików referencji w `tests/assets/corpus/graphics/` z `graphics_reference.py`. Kalibracja: model mieszania
a Pillow `ImageChops` maks. 1, trim a `getbbox` identyczne, poziomy a ImageMagick 6 maks. 1. Tolerancje dotrzymane:
LUT, maski i trim 0, mieszanie ≤ 1, `Normal` bajtowo jak 0.6.2. `WeightByAlpha` usunięte z zakresu.
Build Release bez ostrzeżeń, 5261/5261 testów (110 grafiki), Oracle 155/155, scalar 113/113, grafika bez intrinsics
110/110, package consumer z AotSmoke nowych operacji (trim + Native AOT linux-x64). Pomiary (`docs/performance.md`):
12 MP Levels 10,5 ms, histogram 26 ms (po przeplecionych licznikach, wcześniej 56 ms), mieszanie Multiply 75 ms (po
specjalizacji trybów, wcześniej 117 ms). Macierz 80 przypadków ABBA względem v0.6.2: mediana −0,52%, alokacje maks.
+0,13%; przypadki >5% sprawdzone naprzemiennie w osobnych ALC, bez potwierdzonej regresji (PCX decode dwumodalny,
kodek niezmieniony). Bez nowych ścieżek SIMD; ARM64 nieuruchamiany.

Przygotowanie wydania (2026-10-15): VERSION 0.6.3, `docs/releases/release-0.6.3.md`, PackageReleaseNotes,
README, `docs/README.md` i getting-started. Build Release bez ostrzeżeń, 5261/5261, package consumer 0.6.3 (trim +
AOT, AotSmoke z nowymi operacjami). Bez commita, tagu i publikacji.

## 0.6.4 — adaptery do ekosystemu .NET

Zakres:
- Osobne paczki NuGet obok `PictureSharp`; sama biblioteka zostaje bez zależności i bez zmian w API poza ewentualnymi punktami rozszerzeń potrzebnymi adapterom.
- `PictureSharp.Drawing`: konwersje `Image` ↔ `System.Drawing.Bitmap` (Windows) z poprawnym mapowaniem alfa/stride i jawnym kopiowaniem lub własnością bufora.
- `PictureSharp.Skia`: konwersje `Image` ↔ `SKBitmap`/`SKImage`; przezroczystość prosta (nieprzemnożona), jawna polityka alfa.
- `PictureSharp.AspNetCore`: zwracanie obrazu jako `IResult` (kodowanie do strumienia odpowiedzi, negocjacja formatu WebP/PNG/JPEG według `Accept`, nagłówki `Content-Type`/cache), z limitami i anulowaniem przez `RequestAborted`.
- Każdy adapter w osobnym projekcie z własnym README, wersjonowaniem zgodnym z `VERSION` i zależnością tylko od `PictureSharp` oraz docelowego API.

DoD:
- Testy round-trip pikseli dla każdego adaptera (alfa 0/255 i pośrednia, nieparzyste szerokości, stride) oraz testy zwalniania zasobów.
- Test integracyjny ASP.NET Core na `TestServer`: format, nagłówki, anulowanie, przekroczenie limitu.
- Paczki z `LICENSE` i notices; `PictureSharp` nadal bez `PackageReference`; architektura nie zależy od adapterów (test architektury).
- Konsument z lokalnego feedu buduje się; trimming/AOT sprawdzone tam, gdzie zależność to obsługuje.

Weryfikacja 2026-10-16 (linux-x64): `src/PictureSharp.Drawing` (System.Drawing.Common 10.0.0; `ToBitmap` w
32bppArgb/PArgb/24bppRgb/32bppRgb, `Image.FromBitmap`, rozdzielczość, ujemny stride), `src/PictureSharp.Skia`
(SkiaSharp 3.119.4; `ToSKBitmap`/`ToSKImage` Rgba8888/Bgra8888 × Unpremul/Premul, `FromSKBitmap/FromSKPixmap/FromSKImage`
dla dowolnego typu koloru, źródło Opaque → alfa 255), `src/PictureSharp.AspNetCore` (`ImageResult`,
`Results.Extensions.Image`, `ImageResultOptions`: negocjacja `Accept` z q i wildcardami, `Content-Length`, `Vary`,
`Cache-Control`, HEAD, opcjonalne 406, bufor przed zapisem, `RequestAborted`; `request.ReadImageAsync` → 413 przy
`MaxInputBytes`). Wszystkie konwersje kopiują. API rdzenia bez zmian (migawka niezmieniona). Build 0 ostrzeżeń;
5313/5313 testów, 7 pominiętych (GDI+ tylko na Windows; logika wierszy/stride testowana na pamięci natywnej);
`TestServer`: format, nagłówki, anulowanie w trakcie kodowania, limity. Oracle 155/155. Testy architektury: rdzeń tylko runtime,
adaptery tylko PictureSharp + docelowe API. `verify-package-consumer.sh`: paczki z `LICENSE`/notices/README, jeden
assembly, zależności; konsument z lokalnego feedu (trim + Native AOT linux-x64). CI: job `adapters-windows`; Release
pakuje cztery paczki. CI Validate 38858805444: managed, oracle, package-consumer i adapters-windows (58/58, 0 pominiętych) zielone. Nieuruchomione: ARM64, Trusted Publishing dla nowych identyfikatorów NuGet.

## 0.6.5 — HDR ze zdjęć iPhone: JPEG z gain map

Cel: odczytać rzeczywistą informację HDR z JPEG zawierającego obraz bazowy SDR i mapę wzmocnienia,
udostępnić liniowe piksele HDR oraz umożliwić świadomą konwersję do zwykłego obrazu SDR. Sam odczyt bazowego
JPEG lub podbicie jasności nie oznacza obsługi HDR. Pierwszy etap obejmuje zdjęcia statyczne i eksporty JPEG
z ekosystemu iPhone; HEIF/HEIC, ProRAW/DNG i wideo pozostają poza zakresem.

Formaty i źródła:
- Rozróżnić starszy Apple HDR Gain Map oraz Adaptive HDR oparty na ISO 21496-1; to osobne warianty metadanych
  i rekonstrukcji. Apple opisuje oba podejścia w [WWDC24: Use HDR for dynamic image experiences](https://developer.apple.com/videos/play/wwdc2024/10177/).
- Starszy wariant: mapa luminancji, jej krzywa transferu, `HDRGainMapVersion` i parametry headroom z Apple
  MakerNote według [Applying Apple HDR effect to your photos](https://developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos).
  Nie traktować wzoru Apple jako implementacji ISO 21496-1.
- Dla ISO zweryfikować specyfikację oraz strukturę JPEG/MPF i metadanych na podstawie przypiętej wersji
  [libultrahdr](https://github.com/google/libultrahdr). Narzędzie służy jako zewnętrzna wyrocznia dla wspólnego
  podzbioru ISO; biblioteka PictureSharp pozostaje BCL-only. Zgodność z samym XMP Adobe/Ultra HDR nie jest
  automatycznie zgodnością z Apple. Przed portowaniem sprawdzić licencję i uzupełnić notices.

Zakres:
- Najpierw macierz rzeczywistych wariantów: model iPhone’a, wersja systemu, sposób eksportu, typ gain map,
  przestrzeń barwna, orientacja i rozdzielczości obu obrazów. Nie każdy JPEG z iPhone’a zawiera HDR; zwykły
  eksport SDR musi zostać poprawnie rozpoznany. Prywatne zdjęcia wyłącznie lokalnie; do repo syntetyczne
  odpowiedniki i próbki z prawem redystrybucji, bez GPS. Pobieranie zewnętrznych korpusów wymaga zgody.
- Parser kontenera JPEG: MPF/APP2, odpowiednie APP1/XMP i Apple MakerNote, identyfikacja oraz bezpieczne
  wydzielenie pomocniczego JPEG. Użyć istniejącego dekodera do bazy i mapy; nie zakładać stałego rozmiaru
  mapy ani że każdy dodatkowy obraz jest gain map. Mapa nie jest klatką animacji w `Image.Frames`.
- `Image.Identify` ma opisywać obecność, wariant i deklarowane parametry HDR bez pełnego dekodowania pikseli;
  odróżnić wykrycie deklaracji od potwierdzenia możliwości rekonstrukcji. Zwykłe `Image.Load` nadal zwraca
  bazę SDR z dotychczasowymi pikselami. Odczyt HDR jest jawny, z przeciążeniami strumień/plik i async oraz
  wspólnymi `DecoderOptions` i anulowaniem.
- API pozostaje skupione na `Image`: roboczo rozszerzenie `Image.LoadHdr(...)` zwracające osobny wynik z
  liniowymi kanałami float i opisem przestrzeni barwnej/headroom. `Image`/`ImageFrame` nadal przechowują
  `Rgba32` z prostą alfą; wartości HDR powyżej 1 nie mogą być tam obcinane. Przed implementacją zatwierdzić
  nazwy, własność i zwalnianie buforów, dostęp do wierszy oraz kontrakt braku HDR (jawny błąd lub jawnie
  wybrany fallback SDR). Bez statycznej fasady kodeka i bez ogólnej przebudowy modelu na `Image<TPixel>`.
- Rekonstrukcja najpierw skalarna: osobne modele Apple/ISO, dekodowanie transferu, interpolacja mapy,
  parametry headroom i docelowego zakresu, jednoznaczna jednostka bieli SDR = 1. Obsłużyć sRGB oraz Display P3
  z obsługiwanym ICC; mnożenie w prawidłowej przestrzeni liniowej, bez przedwczesnego clippingu do sRGB.
  Zdefiniować kolejność orientacji, skalowania mapy i konwersji barw, także dla zmniejszonego dekodu JPEG.
- Jawna konwersja wyniku HDR do `Image` SDR: udokumentowany, deterministyczny operator tone mapping i
  polityka gamut mapping, parametry ekspozycji/bieli, końcowe kodowanie sRGB. To osobny wybór względem
  pobrania oryginalnej bazy SDR; wygląd aplikacji Photos nie stanowi numerycznej specyfikacji operatora.
- Zdefiniować współdziałanie z `IgnoreMetadata`, `ColorConversion`, orientacją i miniaturami. Metadane
  niezbędne do rekonstrukcji są danymi dekodera; ignorowanie metadanych użytkowych nie może ich przypadkiem
  usunąć. Nieobsługiwany wariant/profil przy żądaniu HDR → `NotSupportedException`, uszkodzone struktury →
  `ImageFormatException`; brak niejawnego sukcesu z SDR po nieudanej rekonstrukcji HDR.
- Wspólne limity dla bazy i mapy: `MaxInputBytes` na cały kontener, `MaxPixels` na sumę dekodowanych pikseli,
  `MaxMetadataBytes` także na pomocnicze metadane; sprawdzanie rozmiaru bufora float i wszystkich offsetów
  przed alokacją. Mapa nie zwiększa `MaxFrames`. Anulowanie w ograniczonych porcjach dekodu i rekonstrukcji.
- `EditMetadata` bez rekompresji ma zachowywać oba strumienie JPEG oraz poprawiać offsety MPF po zmianie
  długości metadanych. Edycja/usunięcie informacji niezbędnych dla HDR musi mieć jawną politykę i nie może
  pozostawić pozornie poprawnego HDR. Zwykły zapis obrazu SDR nie może kopiować nieaktualnych deklaracji HDR.
- CLI `info`: wariant HDR i parametry; `convert`: jawny wybór bazowego SDR albo tone mapping HDR→SDR.
  Dokumentacja z przykładem dostępu do liniowych pikseli HDR i ograniczeniami odbiorcy/wyświetlacza.
- Poza 0.6.5: tworzenie nowych gain map, enkoder JPEG HDR, pełne przetwarzanie HDR przez istniejące `Mutate`,
  automatyczne utrzymywanie gain map po resize/crop/korekcji, wyświetlanie HDR i integracja GPU. Konwersja
  do PNG/WebP w obecnym modelu daje SDR; zachowanie HDR przy takim zapisie wymaga osobnego przyszłego etapu.

DoD:
- Korpus obu wariantów (Apple i ISO): różne rozmiary map, sRGB/Display P3, orientacje, brak mapy, nieznana
  wersja, brakujące/sprzeczne metadane, obcięcia, błędne offsety MPF, przepełnienia i przekroczenia limitów.
- Przed implementacją zapisać referencje float i ustalić tolerancje osobno dla dekodu JPEG, interpolacji,
  rekonstrukcji oraz tone mapping. Wektory analityczne i niezależny model double; ISO porównane z przypiętym
  libultrahdr, Apple z wynikami ImageIO/Core Image na macOS/iOS, z zapisanymi opcjami i wersją systemu.
  Brak środowiska Apple nie jest zaliczeniem zgodności; zachować reprodukowalne referencje do CI na Linux.
- Testy potwierdzają wartości HDR > 1, tożsamość przy neutralnej mapie/headroom = 1 w odpowiednim modelu,
  poprawną orientację i kolory, deterministyczność, limity i anulowanie. Zwykły JPEG i domyślny odczyt SDR
  zachowują dotychczasowe referencje; API float nie obcina świateł.
- Edycja metadanych zachowuje bajty obu skompresowanych obrazów i odczytywalność HDR przez wyrocznię;
  ścieżka SDR usuwa niespójne deklaracje. Testy własności buforów, Dispose i kompilacji konsumenta.
- Benchmarki małych zdjęć i 12/48 MP: czas, alokacje, szczytowa pamięć i reakcja na anulowanie; regresja
  zwykłego JPEG. Migawka API, XML docs, przykłady, CLI, wspólna bramka jakości i konsument trim/Native AOT.

Kolejność: próbki i kontrakt API → referencje → parser/Identify → Apple → ISO → wynik float i tone mapping →
metadane/CLI/dokumentacja → pomiary i bramka. Etapu nie zamyka samo wykrywanie mapy ani odczyt bazy SDR.

## 0.7.0 — integracja i stabilizacja

Zakres:
- Ukończyć DoD obsługi ICO, wszystkich sześciu obszarów, adapterów z 0.6.4 i JPEG HDR z 0.6.5. Ujednolicić opcje, nazwy, własność buforów, limity, anulowanie i polityki metadanych/koloru.
- Testy end-to-end: tworzenie i odczyt wielowariantowego ICO; miniatura JPEG z orientacją i konwersją ICC; sekwencyjna konwersja animacji z maską i resize; edycja metadanych bez rekompresji; przetwarzanie przy ograniczonych zasobach; odczyt JPEG HDR i jawna konwersja do SDR.
- Uzupełnić CLI i przewodniki o ICO, miniatury, limity, sekwencyjne animacje, kolor, metadane i nowe operacje. Przykłady sprawdzane przez `DocsExamplesTests`.
- Rozszerzyć istniejącą macierz wydajności o nowe ścieżki, czas anulowania, szczytową pamięć i długie animacje; opisać kompromisy jakości i ograniczenia.
- Sprawdzić pakiet NuGet, trimming/NativeAOT w obsługiwanym zakresie i użycie z lokalnego feedu na x64/ARM64. Przygotować notatki migracyjne z 0.5.0, w tym zmianę nazwy pakietu i przestrzeni nazw z RasterSharp na PictureSharp.

DoD: wszystkie wcześniejsze kryteria zamknięte, pełna wspólna bramka jakości zaliczona, dokumentacja i paczka gotowe. Niezrealizowanych punktów nie oznaczamy jako ukończone; zmiana zakresu wymaga jawnej decyzji i aktualizacji planu.

## Wspólna bramka jakości każdego wydania

- Build Release bez ostrzeżeń, testy dotkniętych obszarów podczas pracy, pełne testy Release i Oracle przed zamknięciem wydania. Pominięta wyrocznia nie potwierdza zgodności.
- Korpus syntetyczny, round-trip, testy uszkodzonych/obciętych danych i limitów dla nowych ścieżek; stare referencje pozostają zgodne w trybach domyślnych.
- Nowe SIMD: identyczność ze skalarem na x64 i rzeczywistym ARM64, sprawdzenie fallbacków; równoległość: deterministyczność i zwalnianie zasobów po błędzie.
- Pomiary przed/po na stałych wejściach i ustawieniach zgodnie z `docs/performance.md`. Bez regresji poza ustaloną tolerancją i bez nieuzasadnionego wzrostu pamięci.
- Aktualne XML docs, migawka publicznego API, przykłady, notices i pakiet zawierający `LICENSE` oraz `THIRD-PARTY-NOTICES.txt`, bez zależności biblioteki.
- Po znaczącym kroku wpis w `progres.md`: zmiany, decyzje, wyniki, ograniczenia i następny krok. Plan aktualizujemy po rzeczywistym spełnieniu DoD.

```bash
dotnet build -c Release
dotnet test -c Release
dotnet test -c Release --treenode-filter "/*/*/*/*[Category=Oracle]"
```

## Wzmocnienie domeny i C# 14 — zakończone lokalnie (2026-10-15)

- ImageDimensions, atomowe zatwierdzanie wspólnego płótna przez Image, kontrolowana własność, nieujemne czasy klatek i spójne Dispose.
- Migawki metadanych i jawna edycja orientacji; grupy opcji i wspólne budżety zagnieżdżonych dekoderów; wewnętrzne kontrakty sesji bez implementacji kodeków sekwencyjnych.
- Testy reguł domeny, kontraktów kodeków i sesji, wywołań IL oraz kompilacji konsumenta C# 14. CI push/PR i bramka przed publikacją z wyroczniami i konsumentem pakietu trim/AOT.
- DoD lokalne: Release 0 ostrzeżeń/błędów, 5134/5134, Oracle 150/150, scalar 113/113; paczka i isolated-cache konsument z pełnym trimmingiem oraz Native AOT linux-x64. Macierz 80 przypadków ABBA i 10 naprzemiennych kontroli nie potwierdziła regresji >5%; dokumentacja w docs/ddd-verification.md. ARM64 i zdalny GitHub CI niesprawdzone. Wydanie 0.5.5 przygotowane na polecenie użytkownika. Kodeki sekwencyjne pozostają częścią 0.6.0.
