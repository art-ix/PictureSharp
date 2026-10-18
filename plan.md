# PictureSharp: plan rozwoju do 0.8.0

## Stan i cel

**Bieżąca wersja: 0.7.4 (opublikowana 2026-10-18); 0.7.5 gotowa do wydania.** PictureSharp to biblioteka obrazów dla .NET 10+ w C# 14, bez zależności i
kodu natywnego: WebP, PNG/APNG, JPEG, GIF, BMP, PCX i ICO, animacje (także sekwencyjnie), metadane, zarządzanie
kolorem, operacje graficzne, adaptery (System.Drawing, SkiaSharp, ASP.NET Core) i odczyt HDR z JPEG z gain map.

Dalej: optymalizacja rdzenia — WebP lossy i potok przetwarzania (0.7.5) — potem poprawki SPI
zgłoszone przez wtyczkę HEIF i generowanie gain map (0.8.0). Wtyczka HEIC `PictureSharp.Heif`
(własny port HEVC intra w C#) powstaje równolegle w osobnym repozytorium, z własnym SemVer od 0.1.0. Weryfikacja
Apple na macOS (0.6.6) czeka na dostęp do sprzętu. Pozostałe nowe formaty (CUR, TIFF, QOI, TGA, PNM, AVIF, JPEG XL)
są poza planem.

Decyzje użytkownika (2026-10-16): kolejność rdzenia 0.6.9 → 0.7.0 → 0.8.0; HEIF jako wtyczka z portem dekodera i
enkodera HEVC intra w C#, nie jako wrapper natywnego libheif; wtyczka w osobnym repozytorium z własnym SemVer; rdzeń
0.8.0 = poprawki SPI zgłoszone przez wtyczkę + dalszy HDR.
Decyzja użytkownika (2026-10-18): przed 0.8.0 dwa wydania optymalizacji rdzenia (0.7.4, 0.7.5) bez zmian API i
wyników.

Istniejące API i ograniczenia: `README.md`, `docs/`, XML docs, `tests/assets/PublicApi.approved.txt`. Pomiary:
`docs/performance.md`. Ukończone wydania: [changelog](#changelog), `docs/releases/` i historia Git. Lokalna historia
pracy: `progres.md` (bez commitowania).

## Zasady obowiązujące we wszystkich wydaniach

- `net10.0`, `LangVersion=14.0`, nullable, warnings as errors i XML docs. Publiczne API w `PictureSharp` (model, opcje, konfiguracja i rozszerzenia `Image`), `PictureSharp.Processing`, `PictureSharp.Formats` (SPI wtyczek) i `PictureSharp.Formats.<Format>`; implementacja jest `internal`.
- Biblioteka używa wyłącznie BCL: bez `PackageReference`, P/Invoke, `unsafe` oraz `System.IO.Compression`. Deflate/inflate pochodzi z `Algorithms/Compression/`. `System.Drawing.Primitives` jest dozwolone. Wtyczki mogą używać `System.IO.Compression` do dekompresji (decyzja użytkownika 2026-10-17). Rdzeń zostaje przy własnym deflate/inflate (decyzja użytkownika 2026-10-18, zastępuje plan zniesienia zakazu z 2026-10-17): migracja na `ZLibStream`/`DeflateStream` została zmierzona i wycofana (domyślne kodowanie PNG do 6× wolniejsze, dekodowanie do 1,5× wolniejsze, rozmiary bez zmian); szczegóły w `docs/performance.md`, sekcja „Own deflate versus System.IO.Compression (0.7.2)”.
- `Image` i `ImageFrame` przechowują `Rgba32` z prostą alfą. Bufory robocze z `ArrayPool<T>`, piksele z `GC.AllocateUninitializedArray`; gorące pętle bez alokacji.
- Zachowujemy dotychczasowe wyniki i domyślne zachowanie. Nowe przekształcenia obrazu wymagają jawnej opcji. Ewentualne nowe domyślne limity zasobów muszą być opisane jako zmiana zachowania.
- Uszkodzone dane: `ImageFormatException`; poprawne, nieobsługiwane warianty: `NotSupportedException`. Anulowanie: `OperationCanceledException`. Błędy I/O zachowują właściwy typ. Żadne złośliwe wejście nie może powodować niekontrolowanych wyjątków indeksowania lub przepełnienia.
- Nowe porty WebP bazują na read-only `reference/libwebp-1.6.0/`, z komentarzami źródłowymi i aktualizacją `THIRD-PARTY-NOTICES.txt`. Zachowujemy semantykę C, w tym kolejność operacji `float` i 23-bitowe koszty VP8L.
- Najpierw poprawna wersja skalarna. SIMD x64/ARM64 i równoległość dodajemy po pomiarach; wyniki muszą być identyczne ze skalarem i niezależne od liczby wątków.
- Testy, benchmarki i wyrocznie mogą używać zewnętrznych pakietów. Pobieranie zewnętrznych korpusów wymaga zgody użytkownika. Prywatne zdjęcia pozostają lokalne.
- Commit, push i publikacja wyłącznie na polecenie użytkownika. `VERSION` zmieniamy przy przygotowaniu wydania, nie przy rozpoczęciu etapu.

## Kolejność wydań

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.6.6 | Weryfikacja HDR z ImageIO/Core Image na macOS, usuwanie deklaracji Ultra HDR przy zapisie SDR | Odłożona do dostępności macOS |
| 0.6.9 | HDR przy edycji: gain map w operacjach geometrycznych i zapis JPEG z mapą ISO 21496-1 | Wydana (2026-10-16; bez ARM64) |
| 0.7.0 | Integracja wszystkich obszarów, SPI wtyczek dla gain map i metadanych, stabilizacja API i wydanie | Wydana (2026-10-17; ARM64 zweryfikowane, bez macOS) |
| 0.7.1 | Poprawka SPI zgłoszona przez wtyczkę HEIF: wszystkie wykryte modele gain map przy `AttachGainMap` | Wydana (2026-10-17; bez ARM64) |
| 0.7.2 | Skrócona i uzupełniona dokumentacja, stabilny test alokacji, porządki w planie; bez zmian API i kodu biblioteki | Wydana (2026-10-17; bez ARM64) |
| 0.7.3 | Mniej alokacji w deflate/inflate PNG i przy metadanych PNG; bajty wyjściowe i API bez zmian | Wydana (2026-10-18; bez ARM64) |
| 0.7.4 | Optymalizacja JPEG: entropia (dekoder i enkoder), upsampling i konwersja koloru, ścieżka jednowątkowa; wyniki bitowo bez zmian | Wydana (2026-10-18; bez ARM64) |
| 0.7.5 | Optymalizacja WebP lossy (VP8) i potoku przetwarzania (ICC → sRGB, światło liniowe, kopie pośrednie); wyniki bitowo bez zmian | Gotowa do wydania (bez ARM64) |
| 0.8.0 | Poprawki SPI zgłoszone przez wtyczkę HEIF, gain map w enkoderach wtyczek, generowanie gain map i zapis HDR | Planowana |

Wtyczka `PictureSharp.Heif` ma osobne repozytorium i własne wersje (od 0.1.0); jej zależności od wydań rdzenia
opisuje sekcja [Wtyczka HEIF](#wtyczka-heif--osobne-repozytorium).

Numery określają kolejność, nie terminy. Każde wydanie wymaga własnego DoD oraz wspólnej bramki jakości. Nazwy nowych opcji i metod poniżej są robocze do czasu zatwierdzenia migawki API.

## Zaległe weryfikacje — przed 0.6.9

Krótkie zadania, które nie dodają funkcjonalności, ale blokują zamknięcie bramki 0.7.0:
- ARM64 końcowej wersji 0.6.8 (testy, `*SimdTests`, fallbacki). Orange Pi jest wyłączony poleceniem użytkownika; bez
  jego zgody punkt przechodzi do bramki 0.7.0 i pozostaje otwarty.
  Zamknięte (2026-10-17): ARM64 sprawdzone na wersji 0.7.0 (pełne testy i wariant bez intrinsics).
- NuGet: obecność 0.6.8 wszystkich czterech paczek w indeksie; Trusted Publishing dla `PictureSharp.Drawing`,
  `PictureSharp.Skia` i `PictureSharp.AspNetCore`.
- 0.6.6: decyzja użytkownika (2026-10-16) — testy na macOS odłożone do dostępności sprzętu; etap pozostaje otwarty.
  Decyzja użytkownika (2026-10-17): brak możliwości wynajęcia macOS — 0.6.6 nie blokuje 0.7.0; wydanie 0.7.0 opisuje
  wariant Apple jako niepotwierdzony na Apple.
  Bez porównania z ImageIO/Core Image wariant Apple pozostaje „zgodny z referencjami Linux, niepotwierdzony na Apple”
  i nie jest oznaczany jako zweryfikowany.

## 0.6.6 — weryfikacja HDR na macOS (odłożona)

- Porównać rekonstrukcję wariantu Apple (i ISO) z ImageIO/Core Image na macOS/iOS; zapisać opcje, wersję systemu i
  tolerancje. Brak środowiska Apple nie jest zaliczeniem; referencje dla CI na Linux pozostają reprodukowalne.
- Benchmarki HDR względem ImageIO na tym samym sprzęcie.
- Zapis SDR nie może przenosić nieaktualnych deklaracji HDR (np. XMP `hdrgm` w bazie); przed pracą sprawdzić, co już
  zapewnia obecny enkoder.

## 0.6.9 — HDR przy edycji i zapis JPEG z gain map

Cel: JPEG HDR wczytany z `LoadGainMap` po zmianie orientacji, rozmiaru lub przycięciu można zapisać jako JPEG z gain
map, który odczyta PictureSharp, libultrahdr oraz odbiorcy ISO 21496-1 (iOS 18+, Android, przeglądarki). Domyślny
`Save` pozostaje SDR i bajtowo identyczny z 0.6.8; zapis mapy wymaga jawnej opcji. Wydanie nie tworzy nowych map.

Decyzje zatwierdzone (2026-10-16):
- Wariant A: operacje geometryczne zachowują mapę bez dodatkowej opcji. Zmiana zachowania `image.Hdr`/`HasHdr` po
  `Mutate` (wcześniej `null`, `ToHdr` → `InvalidOperationException`) opisana w notatkach wydania.
- `JpegEncoderOptions.GainMap` typu `JpegGainMapMode`: `None` (domyślnie), `Preserve` (zapis mapy, gdy dołączona i
  dekodowalna, inaczej zwykły JPEG) i `Require` (brak mapy lub wariant niezapisywalny → wyjątek). Dodatkowo
  `GainMapQuality` (1–100, domyślnie 95 jak `kMapCompressQualityDefault` libultrahdr). Nazwy robocze do migawki API.

Konwencje geometrii mapy (ustalone przed implementacją, porównane ze źródłami libultrahdr 2.0.2):
- Mapa pokrywa całe płótno bazy, piksel mapy (i, j) odpowiada prostokątowi bazy [i·sx, (i+1)·sx) × [j·sy, (j+1)·sy),
  gdzie sx = W/mw, sy = H/mh (konwencja środków pikseli, jak `box` w Pillow i model Apple).
- Flip, obrót o 90/180/270° i orientacje EXIF: te same operacje na tablicy mapy (jak `apply_mirror`/`apply_rotate`
  libultrahdr), bit-exact. Przycięcie wyrównane do sx/sy (całkowite współrzędne mapy): wycinek tablicy, bit-exact.
- Rozmiar mapy po operacji: mw' = max(1, ⌊(2·W'·mw + W) / (2·W)⌋), analogicznie mh' (zaokrąglenie połówek w górę,
  arytmetyka całkowita).
- Resize, Thumbnail, tryby Max/Crop/Min/Stretch i przycięcie niewyrównane: resampling mapy w dziedzinie zakodowanej
  (kody 8-bit) przez `Resizer` (port Pillow) z prostokątem źródłowym przeliczonym do współrzędnych mapy (ułamkowym).
  Filtr jak w operacji bazy; `Crop` bez filtra używa `Bilinear`. libultrahdr przycina mapę z obcięciem współrzędnych
  (`(int)(left / ratio)`) i skaluje ją próbkowaniem co całkowity krok, więc dla operacji niewyrównanych jest tylko
  punktem odniesienia jakości, nie wyrocznią bajtową.
- Przekształcona mapa jest próbkowana przy `ToHdr` tak samo jak mapa z pliku (bez śledzenia geometrii źródła), więc
  `ToHdr` przed zapisem i po ponownym wczytaniu zapisanego pliku różni się tylko kompresją mapy.

Zakres:
- Model: `GainMapAttachment` w dwóch niezmiennych stanach: skompresowany (jak dziś, bez dekodu przy ładowaniu) i
  przekształcony (piksele mapy `Rgba32` w jej rozdzielczości + niezmienione parametry). Mapa dekodowana leniwie przy
  pierwszej operacji geometrycznej; `Clone()` współdzieli stan; `MaxPixels` obejmuje mapę jak w 0.6.5.
- Operacje zachowujące mapę: `Resize` (tryby bez dodawania tła), `Thumbnail`, `Crop`, `AutoOrient`, `Rotate` o
  90/180/270°, `Flip`; `TargetSize` i `ApplyExifOrientation` jak dziś. Mapa przechodzi tę samą geometrię przeliczoną do
  swojej rozdzielczości: zachowany stosunek mapa/baza (zaokrąglenie, min. 1 px), przycięcie prostokątem w ułamkowych
  współrzędnych mapy z resamplingiem w dziedzinie zakodowanej mapy. Semantykę zaokrągleń porównać z efektami edycji
  libultrahdr 2.0.2 (`uhdr_add_effect_crop/resize/rotate/mirror`).
- Pozostałe operacje (kolor, ton, filtry, mieszanie, maski, `Pad`/`Canvas`, obrót o dowolny kąt, zmiany ramek) nadal
  usuwają mapę. Polityka opisana w `docs/hdr.md` w jednej tabeli.
- Enkoder JPEG z mapą (ISO 21496-1 jak domyślny build libultrahdr 2.0.2 `appendGainMap`, `UHDR_WRITE_ISO=ON`,
  `UHDR_WRITE_XMP=OFF`; decyzja użytkownika 2026-10-16): baza SOI, APP0 JFIF, EXIF, XMP użytkownika, ICC, APP2 ISO
  (wersje 0/0), DQT/SOF/DHT, APP2 MPF (big-endian, dwa wpisy) tuż przed pierwszym SOS, skan…EOI; mapa SOI, APP2 ISO,
  ICC mapy ze źródła, segmenty mapy bez APPn. Bez generowanego XMP `hdrgm`/GContainer; XMP użytkownika, które deklaruje
  gain map (przestrzenie `hdr-gain-map` lub GContainer), jest pomijane przy zapisie z mapą (opisywałoby nieaktualne
  elementy). Payload ISO kopiowany bajtowo ze źródła (parametry się nie zmieniają). Mapa nieprzekształcona (także po
  `TargetSize`) kopiowana bez rekompresji; przekształcona lub zorientowana przy ładowaniu kodowana z
  `GainMapQuality` (domyślnie 95, `kMapCompressQualityDefault`), mono jako JPEG w skali szarości, RGB jako 4:4:4.
- Warianty: źródło ISO oraz Apple+ISO zapisywane jako ISO (bez Apple MakerNote w nowej deklaracji); samo Apple
  (iOS 14–17) → `Require` daje `NotSupportedException`, `Preserve` zapisuje SDR. Konwersja Apple→ISO poza zakresem.
- SPI dla wtyczki HEIF (decyzja użytkownika 2026-10-16): `ImageInfo.ContainerItemCount` (`int?`, ≥ 0) — liczba
  elementów kontenera zgłaszana przez wtyczki; formaty wbudowane zostawiają null, niezależne od `FrameCount`. Gotowe:
  właściwość, migawka API, test przepływu przez wtyczkę testową. Wtyczka 0.1.0 wymaga PictureSharp 0.6.9.
- Zapis PNG/WebP/innych formatów pozostaje SDR; `EditMetadata` bez zmian. Limity rozmiaru kodowanej mapy i anulowanie
  w resamplingu i kodowaniu mapy.
- CLI: `convert`/`resize` z `-hdr keep` (JPEG→JPEG z mapą); `info` pokazuje mapę po przekształceniu.

Referencje (rozszerzenie `tests/assets/hdr_reference.py`, `python3 -I`; model i tolerancje gotowe 2026-10-16):
- `corpus/hdr_edit/` i `hdr_edit_reference.json`: 228 edycji 21 źródeł (mapy 1/1, 1/2, 1/4, ułamkowe, nieparzyste i
  zaokrąglone w dół; mono i RGB; ISO, Apple, Apple+ISO; orientacje 1–8 przez `AutoOrient` i przy ładowaniu; flip,
  obroty, przycięcie wyrównane i niewyrównane, Resize Stretch/Crop z filtrami Nearest/Bilinear/Bicubic/Lanczos3,
  Thumbnail i łańcuch). Na każdą edycję: oczekiwany rozmiar bazy i mapy oraz kody mapy (`.map`).
- Mapa: operacje tablicowe i wycinki tolerancja 0, resampling ≤ 1 na kanał (model to Pillow, którego portem jest
  `Resizer`, więc oczekiwane 0).
- Rekonstrukcja: `ToHdr` po edycji względem modelu na tej samej bazie i mapie (`.edited.f32`) w tolerancji z 0.6.5.
- Wierność (`.ideal.f32`): ta sama edytowana baza, wzmocnienie próbkowane z oryginalnej mapy w dokładnej pozycji
  źródłowej piksela. Budżet na gładkich mapach: max 0,8, mediana 0,10, p99 0,26 (pierwszy pomiar max 0,539,
  mediana 0,066, p99 0,170, mapa 16 px, plus 50%). Flip/obrót mapy ISO przesuwa wzmocnienie o (1 − 1/skala) piksela
  mapy, bo libultrahdr próbkuje od rogu; wariant Apple (środki pikseli) pozostaje dokładny (0).
- libultrahdr: semantyka edycji porównana ze źródłami (`apply_effects` dekodera: flip/obrót tablicowo jak u nas,
  przycięcie z obcięciem współrzędnych, resize co całkowity krok). Dekodowanie naszych plików przez `ultrahdr_app`
  sprawdzamy na etapie enkodera.

DoD:
- Testy każdej operacji × orientacje EXIF 1–8 × proporcje mapy 1/1, 1/2, 1/4 i nieparzyste rozmiary; mapy mono i RGB;
  źródła ISO, Apple+ISO i samo Apple.
- Round-trip `Load` → `Mutate` → `Save(Preserve)` → `Load`: `HasHdr`, identyczne parametry, piksele HDR w tolerancji;
  bez `Mutate` mapa bajtowo identyczna ze źródłem.
- Domyślny `Save` bajtowo jak 0.6.8; operacje niegeometryczne usuwają mapę; zwykłe JPEG i dotychczasowe referencje bez
  zmian.
- Testy limitów, uszkodzonych map przy zapisie, anulowania i alokacji (resize bez pełnowymiarowych buforów float).
- Pomiary `Load` → `Resize` → `Save` 12/48 MP z mapą i bez; migawka API z samymi dodatkami, `docs/hdr.md`,
  `docs/cli.md`, przykłady w `DocsExamplesTests`, wspólna bramka jakości i konsument trim/Native AOT.
- Poza 0.6.9: generowanie map z `HdrImage`, zapis `HdrImage`, przetwarzanie HDR w float, konwersja Apple→ISO, HDR w
  innych formatach.

Kolejność: decyzje API → referencje → model mapy → operacje geometryczne → enkoder → CLI i dokumentacja → pomiary i bramka.

Stan (2026-10-16): wszystkie kroki wykonane. DoD: 228 edycji (operacje × orientacje 1–8 × mapy 1/1, 1/2, 1/4,
ułamkowe i nieparzyste; mono/RGB; ISO, Apple, Apple+ISO) bajtowo zgodne z modelem; round-trip i kopia mapy bez
rekompresji; domyślny zapis bez zmian; limity, uszkodzone mapy, anulowanie i alokacje; `ultrahdr_app` dekoduje nasze
pliki w budżecie 0.6.5 (baza 4:4:4); pomiary 12/48 MP w `docs/performance.md`; migawka API, `docs/hdr.md`,
`docs/cli.md`, `DocsExamplesTests`. Bramka linux-x64: 7168 testów, fallbacki SIMD, konsument trim/Native AOT.
Otwarte poza DoD 0.6.9: ARM64 (do bramki 0.7.0), weryfikacja na Apple (0.6.6).

## 0.7.0 — integracja i stabilizacja

Zakres:
- Ukończyć DoD obsługi ICO, wszystkich sześciu obszarów, adapterów z 0.6.4, JPEG HDR z 0.6.5 i 0.6.9. Ujednolicić opcje, nazwy, własność buforów, limity, anulowanie i polityki metadanych/koloru.
- SPI wtyczek dla gain map i metadanych (szczegóły w podsekcji poniżej); wbudowane kodeki pozostają na kontraktach
  wewnętrznych.
- Testy end-to-end: tworzenie i odczyt wielowariantowego ICO; miniatura JPEG z orientacją i konwersją ICC; sekwencyjna konwersja animacji z maską i resize; edycja metadanych bez rekompresji; przetwarzanie przy ograniczonych zasobach; odczyt JPEG HDR i jawna konwersja do SDR; JPEG HDR → resize → zapis z mapą → odczyt przez libultrahdr; wtyczka testowa korzystająca z nowego SPI HDR.
- Uzupełnić CLI i przewodniki o ICO, miniatury, limity, sekwencyjne animacje, kolor, metadane, HDR i nowe operacje. Przykłady sprawdzane przez `DocsExamplesTests`.
- Rozszerzyć istniejącą macierz wydajności o nowe ścieżki, czas anulowania, szczytową pamięć i długie animacje; opisać kompromisy jakości i ograniczenia.
- Sprawdzić pakiet NuGet, trimming/NativeAOT w obsługiwanym zakresie i użycie z lokalnego feedu na x64/ARM64. Przygotować notatki migracyjne z 0.5.0, w tym zmianę nazwy pakietu i przestrzeni nazw z RasterSharp na PictureSharp.
- Rozstrzygnąć zaległe weryfikacje: ARM64 (wymaga zgody użytkownika na Orange Pi). 0.6.6 (macOS) poza bramką 0.7.0 — decyzja użytkownika 2026-10-17.

Stan (2026-10-17): testy end-to-end w `tests/PictureSharp.Tests/Core/EndToEndTests.cs` (ICO, miniatura JPEG z
orientacją i ICC, sekwencyjna animacja z maską i resize, metadane bez rekompresji, ograniczone zasoby, JPEG HDR → SDR;
HDR → resize → zapis → libultrahdr w `HdrUltraHdrOracleTests`, wtyczka SPI HDR w `PluginSpiTests`). CLI: `metadata`,
`resize -stream`, `-srgb`/`-max-pixels`/`-threads`; przewodniki: cli, hdr, indeks z ICO,
`docs/migration-from-0.5.md`. Macierz: `integration-probe` (12 MP, czas/alokacje/anulowanie/szczytowy RSS względem
0.6.9, bez regresji) i 10 000 klatek w `sequence-probe` (`docs/performance.md`). Przegląd API przed
zamrożeniem migawki (2026-10-17): opcje, nazwy, `MaxDegreeOfParallelism`, przeciążenia z `CancellationToken` i
ścieżki zapisu z `EncoderOptions.Configuration` spójne; bez zmian sygnatur, poprawiony tylko opis `IImageFormatModule`.
Bramka: linux-x64 7206/7213 (7 GDI+ pominiętych), Oracle 192/192, bez AVX2 i bez intrinsics 6963/6963, paczki
trim/AOT bez ostrzeżeń; ARM64 (Orange Pi, za zgodą użytkownika) 6895/7075 (180 pominiętych: brak narzędzi Oracle,
GDI+) i wariant bez intrinsics. Notatki: `docs/releases/release-0.7.0.md`. DoD spełnione poza 0.6.6 (macOS).

### SPI wtyczek w 0.7.0

Audyt SPI (2026-10-16, stan 0.6.8): dekodowanie i kodowanie HEIC SDR jest możliwe na obecnym publicznym API.
- `IImageFormatModule`: detekcja `ftyp` mieści się w `HeaderSize` ≤ 4096. Dostępne są `ImageFormat.Create`, rejestracja
  przez `ImageConfigurationBuilder.AddFormat` oraz `SaveAsHeic`/`ToHeic` jako extension blocks w paczce wtyczki.
- `Identify` zwraca publiczny `ImageInfo` (wymiary, alfa, głębia, orientacja, `Hdr` z `init`). `Decode` zwraca
  `Image` z publicznymi pikselami i `ImageMetadata` (`IccProfile`, `Exif`, `Xmp`, `SetBlock`; EXIF parsowany przez rdzeń).
- `ImageDecodeContext` daje `ReservePixels`, `ReserveMetadata`, token, `Options` i `DecodeEmbedded`. Rdzeń sam stosuje
  `TargetSize`, `ApplyExifOrientation`, `ColorConversion` i limity klatek/pikseli oraz normalizuje wyjątki wtyczki
  (`ModuleAdapters.cs`, `ImageDecodeService.cs`).
- Kolor nclx bez ICC wyrażają `Chromaticities` (+ `Gamma` dla krzywych potęgowych); same `Chromaticities` oznaczają
  krzywą sRGB (`ColorDescriptionResolver`). Nie obejmuje to PQ/HLG.
- Enkoder: `IImageFormatEncoder<TOptions>` z rekordem pochodnym od `EncoderOptions`. ASP.NET Core
  `ImageResultOptions.Formats` przyjmuje każdy kodowalny format. Brak refleksji, więc trim/AOT bez przeszkód.

Luki, których wtyczka nie obejdzie (typy są `internal`):

| Luka | Stan w 0.6.8 | Potrzebne wtyczce do | Wydanie |
| --- | --- | --- | --- |
| Dołączanie gain map przez wtyczkę | `GainMapAttachment`, `IGainMapDecoder`, `DecodedImage.GainMap` internal | `ToHdr`/`ToneMap` dla HEIC (Apple `hdrgainmap`, ISO `tmap`) | 0.7.0 |
| Parsowanie metadanych gain map poza JPEG | `ReadIso`/`ReadApple` związane z `JpegHeader` | `Identify` (`HdrInfo.Headroom`) i dekod | 0.7.0 |
| Kolor bazy HDR z opisu metadanych | `JpegGainMapReader.ReadBaseColor`, tylko ICC z JPEG | HEIC z ICC lub nclx | 0.7.0 |
| Metadane bez dekodu | `IImageMetadataCodec` internal; wtyczki → `NotSupportedException` | `ReadMetadata`/`EditMetadata` HEIC | 0.7.0 (odczyt; zapis opcjonalny) |
| Liczba elementów kontenera w Identify | `ImageInfo` miało tylko `FrameCount`; to liczba klatek, nie elementów HEIF | DoD wtyczki 0.1.0: liczba elementów bez traktowania kafli/alfy/metadanych jako animacji | Zamknięte w 0.6.9: `ImageInfo.ContainerItemCount` (`int?`, null dla formatów wbudowanych) |
| Konfiguracja przy zapisie | `Save`/`Encode`/`CreateWriter` szukają enkodera tylko w `ImageConfiguration.Default`; odczyt ma `DecoderOptions.Configuration` | `SaveAsHeic`/`ToHeic` i `Save(..., HeifEncoderOptions)` bez podmiany globalnej konfiguracji (testy wtyczki wołają enkoder bezpośrednio) | 0.7.0 (zgłoszone przez wtyczkę 2026-10-16) |
| Kontrakt orientacji kontenera | nieopisany | `irot`/`imir` bez podwójnego obrotu z EXIF | 0.7.0 (dokumentacja) |
| Wszystkie wykryte modele gain map | `AttachGainMap` ustawia `HdrInfo.Detected` tylko na `metadata.Kind` | HEIC z iOS 18 (ISO `tmap` i Apple `hdrgainmap` przy jednej mapie) zgłasza `Detected = Iso21496`, a JPEG z tego samego zdjęcia `Iso21496, Apple` | 0.7.1 (zgłoszone przez wtyczkę 2026-10-17) |
| Gain map i serializacja ISO w enkoderze | internal | zapis HEIC z gain map | 0.8.0 |
| Rozmiar źródłowy przy mniejszym wyniku | `DecodedImage.StoredSize` internal | miniatura z elementu `thmb` | 0.8.0, jeśli wtyczka wykaże zysk |
| Opcje dekodera per wtyczka | `DecoderOptions.Ico/Png/WebP` zamknięte | np. wybór elementu | 0.8.0, jeśli potrzebne |
| CICP PQ/HLG | brak | HDR10 HEIC z Androida | poza 0.8.0 |

Zakres (nazwy robocze, do zatwierdzenia przed implementacją):
0. Konfiguracja zapisu: `EncoderOptions.Configuration` (`ImageConfiguration?`, domyślnie `ImageConfiguration.Default`
   w chwili wywołania), jak `DecoderOptions.Configuration`; używana przez `Save`, `SaveAsync`, `Encode` i
   `CreateWriter` z opcjami.
1. Format-neutralny model gain map: publiczne `PictureSharp.Formats.GainMapMetadata` z
   `ParseIso(payload, mapChannels, alternateIcc)`, `ParseApple(exif, mapXmp)`, `Kind`, `Headroom` i
   `ToHdrInfo(mapSize)`. Opakowuje `GainMapParameters`. Parsery `ReadIso`/`ReadIsoVersion`/`ReadApple`/`AppleHeadroom`
   przeniesione z `JpegGainMapReader.Metadata.cs` do wspólnego miejsca (np. `Infrastructure/Codecs/GainMaps/`); JPEG
   korzysta z nich dalej bez zmiany wyników.
2. Źródło mapy wtyczki: publiczne `GainMapImage { Size; Image Decode(maxDegreeOfParallelism, cancellationToken) }`
   (pełny rozmiar, mono lub RGB) z adapterem do `IGainMapDecoder`. `HdrService` dla map wtyczek używa skali 1 i zgłasza
   błędy z `SourceFormat` zamiast stałego `ImageFormat.Jpeg`.
3. `ImageDecodeContext.AttachGainMap(GainMapMetadata, GainMapImage)` i
   `ImageDecodeContext.DeclareGainMap(detected, corrupt, reason)`. Kontekst zbiera załącznik, a `ModuleDecoder` zwraca go
   w `DecodedImage.GainMap`, więc `TargetSize`, orientacja i `ColorConversion` działają jak dla JPEG. Mapa liczy się do
   `MaxPixels` i jest pomijana bez `LoadGainMap`.
4. Kolor bazy: `BaseSpace`/`BaseLinearization` liczone z rozwiązanego opisu koloru obrazu (ICC albo
   `Chromaticities`/`Gamma`), wspólnie dla JPEG i wtyczek.
5. Metadane bez dekodu: opcjonalny `IImageFormatModule.MetadataCodec => null` z publicznym
   `IImageFormatMetadataCodec { Read(data, context); Rewrite(...) }`; `Rewrite` domyślnie rzuca `NotSupportedException`.
   `CodecResolver.GetMetadataCodec` używa adaptera; atomowy zapis, limity i anulowanie z `ImageMetadataService`.
6. Dokumentacja kontraktu w `docs/extending-picturesharp.md`:
   - orientacja: wtyczka stosuje transformacje kontenera i ustawia `Metadata.Orientation` tak, by `ApplyExifOrientation`
     nie obracał drugi raz;
   - mapowanie nclx, gain map, metadane i wątki;
   - polityka zgodności SPI: zmiany łamiące tylko w wersji minor, z notatką migracyjną i migawką API.

Stan (2026-10-17): punkty 0–6 zaimplementowane z nazwami roboczymi z tej listy (`DeclareGainMap(detected, reason,
corrupt)`, `IImageFormatMetadataCodec.Rewrite` z domyślnym `NotSupportedException`, `GainMapImage` jako klasa
abstrakcyjna). Parsery w `Algorithms/Hdr/GainMapMetadataParser.cs`, kolor bazy w `Algorithms/Hdr/HdrBaseColor.cs`;
JPEG bez zmian wyników. Testy `PluginSpiTests` (wtyczka `GainMapContainerFormat`): `ToHdr` bitowo równe ścieżce JPEG
dla 11 plików korpusu (ISO, Apple, orientacje, P3, przestrzeń alternatywna), `ColorConversion`, `TargetSize` i
edycje, limity, uszkodzone i nieobsługiwane deklaracje, metadane bez dekodu, zapis przez `EncoderOptions.Configuration`.
Migawka API z samymi dodatkami. `verify-package-consumer.sh` sprawdza wtyczkę używającą nowego SPI (zapis z `EncoderOptions.Configuration`, gain mapa, kodek metadanych) pod pełnym trimmingiem i Native AOT — DoD SPI spełnione.

DoD SPI:
- Wtyczka testowa w `tests/PictureSharp.Tests` (kontener z bazą JPEG/PNG, mapą JPEG i payloadem ISO) daje `ToHdr`
  bitowo równe ścieżce JPEG dla tych samych danych.
- Testy `TargetSize`, orientacji, `ColorConversion`, limitów, uszkodzonych deklaracji oraz `ReadMetadata`/`EditMetadata`
  przez wtyczkę.
- Zwykły JPEG HDR bez zmian bajtów i pikseli (`hdr_reference`, wyrocznia libultrahdr).
- Testy architektury, `ConsumerCompilationTests`, migawka API z samymi dodatkami, `verify-package-consumer.sh` z
  wtyczką używającą nowego SPI.

DoD: wszystkie wcześniejsze kryteria zamknięte, pełna wspólna bramka jakości zaliczona, dokumentacja i paczka gotowe. Niezrealizowanych punktów nie oznaczamy jako ukończone; zmiana zakresu wymaga jawnej decyzji i aktualizacji planu.

## 0.7.1 — poprawka SPI gain map (zgłoszona przez wtyczkę HEIF)

Zgłoszenie (2026-10-17, wtyczka 0.4.0): plik HEIC z iOS 18 ma przy jednej mapie metadane ISO 21496-1 (`tmap`) i Apple
(`hdrgainmap` z XMP), a `ImageDecodeContext.AttachGainMap` zapisuje w `HdrInfo.Detected` tylko wybrany model. `Image.Hdr`
dla HEIC różni się więc od JPEG z tego samego zdjęcia (`Iso21496, Apple`). `Identify` może już zgłosić oba modele
(`ToHdrInfo(size) with { Detected = … }`), ale wtyczka tego nie robi, by `Identify` i `Load` były spójne.

Zakres (nazwa robocza): przeciążenie `AttachGainMap(GainMapMetadata metadata, GainMapImage map, HdrGainMapKinds detected)`;
`detected` musi zawierać `metadata.Kind` (inaczej `ArgumentException`), `Selected` pozostaje `metadata.Kind`. Istniejąca
sygnatura bez zmian (zgodność binarna), migawka API z samym dodatkiem.

Stan (2026-10-17): przeciążenie `AttachGainMap(metadata, map, detected)` zaimplementowane (stara sygnatura deleguje z
`metadata.Kind`; `ArgumentException` bez `metadata.Kind` lub z nieznanymi flagami), wtyczka testowa
`GainMapContainerFormat` zgłasza modele jak czytnik JPEG, a `ToHdr_EqualsTheJpegPath` porównuje całe `Image.Hdr` z JPEG
(11 przypadków). Migawka API z samym dodatkiem, `docs/extending-picturesharp.md` zaktualizowane. Przygotowanie wydania:
notatki `docs/releases/release-0.7.1.md`, `VERSION` 0.7.1, `PackageReleaseNotes` (adaptery bez zmian). Bramka
linux-x64: pełny zestaw 7210/7217 (7 GDI+), Oracle 192/192, bez AVX2 i bez intrinsics 6967/6967,
`verify-package-consumer.sh` exit 0 bez ostrzeżeń (nupkg 0.7.1). ARM64 nieuruchamiane (zmiana bez kodu zależnego od
platformy). Wydana 2026-10-17 (tag `v0.7.1`, GitHub Release, NuGet).

DoD: testy `PluginSpiTests` dla obu modeli (`Detected` jak w JPEG, `Selected` i wynik `ToHdr` bez zmian), walidacja
argumentu, dokumentacja w `docs/extending-picturesharp.md`, wspólna bramka jakości. Wtyczka HEIF przechodzi na
przeciążenie w kolejnej wersji wymagającej 0.7.1.

## 0.7.2 — dokumentacja i stabilizacja testów

Wydanie serwisowe bez zmian publicznego API i bez zmian w kodzie biblioteki (migawka API identyczna z 0.7.1). Paczki
wychodzą ponownie ze względu na dokumentację (README paczek, opisy, notatki wydania).

Zakres:
- Dokumentacja (już na `main` po `v0.7.1`): przewodniki w `docs/` skrócone o ~30% słów (`057b816`), różnica `Mutate`
  i `Clone` w getting-started (`cf6e138`), wzmianka o HEIC/HEIF przez wtyczkę `PictureSharp.Heif` (`b9a2c5f`).
  Przed wydaniem jeszcze jeden przegląd: spójność po cięciach (README repozytorium, `src/*/README.md` paczek, linki z
  notatek wydań do nagłówków), brak treści o funkcjach, których nie ma.
- Niestabilny test `AllocationTests.LosslessWebPEncoding_PoolsHistograms`: w CI (Validate 39021033764) 4 783 968 B
  przy limicie 3 MiB mimo `[NotInParallel]`. Prawdopodobna przyczyna: `ArrayPool.Shared` oddaje tablice przy GC gen2,
  więc drugi przebieg alokuje histogramy od nowa. Poprawka tylko w teście: `Measure` jako minimum z kilku przebiegów
  (to samo dla pozostałych budżetów w klasie), limity bez zmian; potwierdzić kilkoma przebiegami pełnego zestawu.
- Porządki w `plan.md`: bieżąca wersja, statusy 0.7.x, changelog 0.7.0–0.7.2; punkt SPI w 0.8.0 (`ReportStoredSize`,
  opcje dekodera per wtyczka) oznaczony jako oczekujący na zgłoszenie z wtyczki, a nie zobowiązanie wydania.
- Przygotowanie wydania: `docs/releases/release-0.7.2.md` (krótkie: Docs, Tests), `VERSION` 0.7.2,
  `PackageReleaseNotes` biblioteki i adapterów, numer wersji w getting-started.

Stan (2026-10-17): `AllocationTests.Measure` zwraca minimum z trzech przebiegów po rozgrzewce (limity bez zmian);
10 kolejnych przebiegów `AllocationTests` 16/16. Przegląd docs: 84 fragmenty C# z `docs/`, README i `src/*/README.md`
kompilują się z rdzeniem i adapterami (pominięty tylko WPF; fragmenty `PictureSharp.Drawing` w osobnym projekcie), 0
zepsutych linków i kotwic w 37 plikach, wiersz HEIC/HEIF w README zgodny z `PictureSharp.Heif` 0.5.0 na NuGet.
Notatki `docs/releases/release-0.7.2.md`, `VERSION` 0.7.2, `PackageReleaseNotes`, getting-started. Bramka linux-x64:
build bez ostrzeżeń, pełny zestaw z Oracle 7210/7217 (7 GDI+), `verify-package-consumer.sh` exit 0 bez ostrzeżeń
(nupkg 0.7.2). Wydana 2026-10-17 (tag `v0.7.2`, GitHub Release, NuGet); Validate w CI zielone z nowym testem.

DoD:
- Wszystkie fragmenty C# z `docs/` i README kompilują się z rdzeniem i adapterami (skrypt ekstrakcji jak przy przeglądzie
  0.7.1), `DocsExamplesTests` zielone, 0 zepsutych linków i kotwic (docs, README, `src/*/README.md`, releases).
- Test alokacji stabilny: 10 kolejnych przebiegów `AllocationTests` i pełny zestaw w CI bez porażki.
- Bramka: build Release bez ostrzeżeń, pełne testy i Oracle, `verify-package-consumer.sh` (nupkg 0.7.2 z README).
  Bez przebiegów bez SIMD i bez ARM64 — kod biblioteki się nie zmienia.

## 0.7.3 — mniej alokacji w PNG

Wydanie serwisowe bez zmian publicznego API; bajty wyjściowe kodowania bez zmian. Po pomiarze z 0.7.2 (własny deflate
kontra `ZLibStream`) zostały różnice w alokacjach, które da się usunąć bez utraty szybkości.

Zakres:
- `Deflater`: tablice symboli bloku (3 × 128 KiB) z `ArrayPool`, zwracane w `Dispose`; kodowanie długości kodów (RLE)
  w buforze na stosie zamiast `List` w każdym bloku.
- `Inflater`: tablice Huffmana bloków dynamicznych budowane ponownie w tych samych buforach
  (`HuffmanTableBuilder.BuildInto`); `Build` dla WebP bez zmian.
- Metadane PNG: iCCP i iTXt rozpakowywane bez kopii do nowej tablicy (`Inflater.InflateZlib(ReadOnlySpan<byte>, …)`),
  profil ICC kompresowany przez `CompressZlibPooled`, słowo kluczowe XMP jako wspólna stała `u8` w `PngConstants`.

Stan (2026-10-18): `PngCompressionBenchmarks` przed/po w `docs/performance.md` (alokacje −0,38 MB na kodowanie,
−0,3–0,8 MB na dekodowanie, czasy bez zmian). Bramka linux-x64: build bez ostrzeżeń, pełny zestaw 7210/7217 (7 GDI+),
Oracle 192/192, bez AVX2 i bez intrinsics 6535/6535, `verify-package-consumer.sh` exit 0 bez ostrzeżeń (nupkg 0.7.3).
ARM64 nieuruchamiane (zmiana bez kodu zależnego od platformy). Wydana 2026-10-18 (tag `v0.7.3`, GitHub Release, NuGet).

## Optymalizacja rdzenia — zasady dla 0.7.4 i 0.7.5

Dwa wydania serwisowe bez zmian publicznego API (migawka identyczna z 0.7.3) i bez zmian wyników: piksele dekoderów,
bajty enkoderów i wyniki operacji bitowo równe 0.7.3 we wszystkich trybach, także z wyłączonym AVX2 i bez intrinsics.
Wybór miejsc wynika z dotychczasowych pomiarów (`docs/performance.md`): JPEG i WebP lossy to najczęstsze ścieżki
użytkownika i największe odstępy od bibliotek natywnych; PNG, WebP lossless i GIF są już na poziomie odniesień.

- Najpierw profil, potem zmiana: przed każdym krokiem pomiar etapów (liczniki czasowe tylko lokalnie, poza repo) na
  stałych wejściach; kolejność kroków według udziału w czasie, nie według listy poniżej.
- Pomiary jednowątkowe i domyślne, BenchmarkDotNet dla wyników do dokumentacji, `matrix-probe` (80 przypadków, ABBA)
  jako kontrola regresji pozostałych ścieżek, alokacje z `AllocationTests` bez wzrostu.
- Porty z kodu C referencji (libjpeg-turbo, libwebp 1.6.0) z komentarzem źródłowym, ta sama arytmetyka; ścieżki SIMD
  w plikach `*.Simd.cs`/`*.X86.cs`/`*.Arm.cs`, testy identyczności ze skalarem.
- Zmiana, która nie daje co najmniej 5% na mierzonej ścieżce albo pogarsza inną o ponad 3%, jest wycofywana i
  opisywana w `progres.md` (jak migracja na `ZLibStream` w 0.7.2).
- Nowe SIMD sprawdzone na rzeczywistym ARM64 (Orange Pi, za zgodą użytkownika); bez zgody wydanie opisuje ARM64 jako
  niezweryfikowane.

## 0.7.4 — optymalizacja JPEG

Stan wyjściowy (0.4.6/0.7.0, x64, Pillow 10.2.0 jako odniesienie libjpeg-turbo): dekodowanie 1920×1440 16,9 ms wobec
10,5 ms (1,6×), progresywne 36,3 wobec 28,7 ms, 12 MP 67,7 wobec 52,4 ms; zapis q75 4:2:0 na jednym wątku 49 ms wobec
6,7 ms. `Image.Load` 12 MP w `integration-probe` 72–76 ms. Zgodnie z wcześniejszą diagnozą dominuje sekwencyjny
Huffman; dekodowanie dotyka też miniatur (`TargetSize`), HDR (baza i mapa) i adapterów.

Zakres (do potwierdzenia profilem):
1. Profil etapów na wejściach z 0.4.6 i `integration-probe` (baseline 4:2:0 i 4:4:4, progresywny, 12 MP, miniatura
   256 px): entropia, IDCT, upsampling, konwersja koloru, kopie i alokacje; wynik w `progres.md` przed zmianami.
2. Dekoder entropii: szybka ścieżka `HUFF_DECODE_FAST`/`decode_mcu_fast` z libjpeg-turbo `jdhuff.c` (64-bitowy bufor
   bitów, łączona tablica podglądu kod + rozszerzenie dla krótkich kodów, wykrywanie markera poza pętlą gorącą,
   wolna ścieżka bez zmian semantyki przy uszkodzonych danych); dla progresywnego odpowiedniki z `jdphuff.c`
   (`decode_mcu_AC_first`/`AC_refine`).
3. Upsampling i konwersja: wektorowy fancy upsampling h2v1/h2v2 (`jdsample.c`, ta sama arytmetyka zaokrągleń) i
   YCbCr→RGBA w tym samym przebiegu wiersza MCU, bez pełnowymiarowych planów pośrednich tam, gdzie dziś istnieją.
4. Enkoder: `encode_one_block` z `jchuff.c` z 64-bitowym `put_buffer` i zliczaniem zer przez `LeadingZeroCount`;
   wektorowe RGB→YCbCr i downsampling 4:2:0 (`jccolor.c`, `jcsample.c`) na ścieżce jednowątkowej.
5. Równoległość dekodera (opcjonalnie, po krokach 2–4): restart intervals, jeśli plik je ma — niezależne segmenty
   entropii dekodowane równolegle, wynik niezależny od liczby wątków.

DoD:
- Piksele dekodera i bajty enkodera bitowo równe 0.7.3 na całym korpusie, referencjach libjpeg-turbo (`reference.json`,
  miniatury `TargetSize`), HDR (`hdr_reference`, `hdr_edit`) i zdjęciach `Category=Local`; testy uszkodzonych i obciętych
  danych, markerów w środku skanu i limitów bez nowych typów wyjątków; fuzz `RandomMutations` dla JPEG.
- Pomiar przed/po (BenchmarkDotNet, `ThumbnailBenchmarks`, `integration-probe`): cel dekodowania baseline 1T ≤ 1,3×
  libjpeg-turbo, zapis q75 1T ≤ 3× (dziś ~7×); niespełniony cel opisany w notatkach z przyczyną, bez blokowania
  wydania, jeśli zysk jest ≥ 5%.
- Brak regresji w `matrix-probe` powyżej 3%, alokacje bez wzrostu, testy SIMD ze skalarem na x64 i ARM64.
- `docs/performance.md` (nowa sekcja 0.7.4), notatki wydania, wspólna bramka jakości.

Stan (2026-10-18): profil etapów zmienił kolejność kroków (najpierw zbieranie próbek i kolor w enkoderze oraz
upsampling i YCbCr→RGBA w dekoderze, potem entropia). Dekoder: szybka ścieżka MCU (`decode_mcu_fast`, 64-bitowy bufor,
podgląd 11 bitów z wartością współczynnika, tablice z puli), szybkie AC first/refine w progresywnym (maska niezerowych),
IDCT AVX2 na `pmaddwd` dla dwóch bloków, wektorowy fancy upsampling i YCbCr→RGBA. Enkoder: zbieranie próbek bez
przycinania we wnętrzu, wektorowe RGB→YCbCr i kwantyzacja (dzielenie float, dokładne dla wyjść DCT); krok 5
(równoległe restart intervals) i 64-bitowy `put_buffer` pominięte — entropia enkodera baseline to ≤ 6 ms. Wycofany
skrót IDCT dla bloków z samym DC (< 5%). Wyniki bitowo równe 0.7.3 (≈ 5000 dekodów i kodowań, także uszkodzone i
obcięte dane, w trybach AVX2, bez AVX2 i bez intrinsics). BenchmarkDotNet 1T: dekod 1920×1440 4:2:0 18,5 → 9,3 ms
(1,43× Pillow; cel 1,3× niespełniony, dominuje entropia), 12 MP 108 → 54 ms (1,06×), progresywny 33 → 19 ms (na
poziomie Pillow); zapis q75 48,6 → 14,9 ms (2,6×, cel ≤ 3× spełniony). Alokacje bez wzrostu. Bramka linux-x64: build bez
ostrzeżeń, pełny zestaw 7213/7220 (7 GDI+), Oracle 192/192, bez AVX2 i bez intrinsics 6970/6970. `matrix-probe`
(na polecenie użytkownika tylko przypadki JPEG, ABBA): dekodowanie −45…−54%, kodowanie −14…−67%, alokacje bez wzrostu.
ARM64 nieuruchomione. Notatki `docs/releases/release-0.7.4.md`, `VERSION` 0.7.4. Wydana 2026-10-18 (tag `v0.7.4`,
GitHub Release, NuGet).

## 0.7.5 — optymalizacja WebP lossy i potoku przetwarzania

Stan wyjściowy (0.5.0, x64, jeden wątek, libwebp 1.6.0 z SIMD): kodowanie lossy 1,5–2,1× wolniejsze od libwebp
(12 MP 1530 wobec 741 ms), dekodowanie 1,5–2,1× (12 MP 136 wobec 90 ms). Ścieżka użytkownika „Load z orientacją i ICC →
`Resize(1600, 0)` → WebP q80” trwa 430–440 ms i alokuje 156,5 MiB; `ConvertToSrgb()` 12 MP 38 ms, resize w świetle
liniowym 1,5–1,7× wolniejszy od domyślnego.

Zakres (do potwierdzenia profilem):
1. Profil enkodera i dekodera VP8 oraz ścieżki integracyjnej (analiza, decyzja trybów, trellis, tokeny, kodowanie
   arytmetyczne; dekoder: współczynniki, rekonstrukcja, filtr, YUV→RGBA; potok: dekod, ICC, resize, kodowanie).
2. Enkoder VP8: brakujące wektorowe porty z `enc_sse2.c`/`enc_sse41.c` (dziś są `FTransform`, `ITransform`, `Disto`,
   `Sse`, `QuantizeBlock`): `CollectHistogram` (analiza), predykcje `Intra16Preds`/`IntraChromaPreds`/`Intra4Preds`,
   `SSE16x16`/`SSE8x8`/`SSE4x4`, `Mean16x4`; bajty identyczne z `cwebp` 1.6.0 jak dziś.
3. Dekoder VP8: predykcje intra z `dec_sse2.c`/`dec_sse41.c` (poza obecnym TrueMotion), pętla `GetCoeffs` według
   profilu, YUV→RGBA w tym samym przebiegu co filtr wiersza makrobloków (mniej przejść po pamięci).
4. Potok: `ConvertToSrgb` (matrix/TRC) z wektorową macierzą i krzywymi tablicowymi tam, gdzie zysk jest mierzalny;
   resize w świetle liniowym bez pełnowymiarowego bufora `Vector4` (pasma wierszy); mniej pełnowymiarowych kopii
   między dekodem, `ApplyExifOrientation`, ICC i resize (cel: alokacje ścieżki integracyjnej ≤ 110 MiB).
5. Kandydat z 0.6.7: liniowe tryby mieszania `DrawImage` ograniczone lookupami sRGB — tylko jeśli profil wykaże
   tańszą deterministyczną formę bez zmiany wyników.

DoD:
- Bajty WebP lossy identyczne z 0.7.3 i `cwebp` 1.6.0 tam, gdzie dziś są; piksele dekodera zgodne z `dwebp`
  (`reference.json`), wyniki ICC, resize i mieszania bitowo równe 0.7.3, także bez AVX2 i bez intrinsics.
- Pomiar przed/po: cel kodowania lossy 1T ≤ 1,5× libwebp, dekodowania ≤ 1,3×, ścieżka integracyjna −20% czasu;
  niespełniony cel opisany w notatkach (jak w 0.7.4).
- Brak regresji w `matrix-probe` powyżej 3%, `AllocationTests` bez wzrostu, testy SIMD ze skalarem na x64 i ARM64,
  anulowanie i deterministyczność równoległości bez zmian.
- `docs/performance.md` (sekcja 0.7.5), notatki wydania, wspólna bramka jakości.

Stan (2026-10-18): punkt wyjścia zmierzony od nowa na kodzie 0.7.4 (wcześniejszy akapit łączy dane z 0.5.0, 0.6.2 i
0.7.0): kodowanie 1T kodim23 45,5 ms / 12 MP 1673 ms wobec libwebp 22,9 / 787 ms (2,0× / 2,1×), dekodowanie 12 MP 142
wobec 100 ms, ścieżka integracyjna 355 ms i 153,4 MiB. Profil i mikrobenchmark prymitywów względem `libwebp.a`
wskazały 32-bitowe pasy (`pmulld`) zamiast 16-bitowych libwebp. Enkoder: porty `enc_sse2.c`/`enc_sse41.c` w 16 bitach
(`FTransform`, `FTransform2`, `CollectHistogram`, `SSE4x4`, `TTransform`, `QuantizeBlock`, `ITransform` z kontrolą
zakresu), predykcje 4×4/8×8/16×16, wektorowe `IsFlat`/`Last`, jedna tablica tokenów z puli, inwarianty `PickIntra4`.
Dekoder: stan dekodera arytmetycznego w zmiennych lokalnych w `GetCoeffs`, kopie bloków bez narzutu. Potok: obrót w
kafelkach 32×32, równoległe `ConvertToSrgb` (w miejscu przy dekodowaniu z `ToSrgb`), resize w świetle liniowym w
pasmach wierszy. Kroki 1–4 zrealizowane, krok 5 (`DrawImage` liniowe) pominięty. Naprawiony błąd 0.7.4:
niedeterministyczne wielowątkowe kodowanie lossy (wyścig na `MaxEdge`, ~1% kodowań m6). Wyniki bitowo równe 0.7.4
(≈ 2800 wyjść w trybach AVX2, bez AVX2 i bez intrinsics; bajty identyczne z `cwebp`). Pomiary 1T: kodowanie kodim23
31,3 ms (1,36×), 12 MP 1208 ms (1,54×, cel 1,5× nieznacznie niespełniony), dekodowanie 12 MP 129,9 ms (1,29×, cel
spełniony), kodim23 3,2 ms (1,59×), małe obrazy −8…−9%; ścieżka integracyjna 210–215 ms (−40%), 106,1 MiB (cel ≤ 110 spełniony); resize
liniowy 12 MP ½ 21 → 15–16 ms. Pozorna regresja dekodowania małej grafiki okazała się artefaktem rozgrzewki JIT w `webp-probe` (poprawiona: ≥ 1 s).
`matrix-probe`: WebP lossy −6…−31%, alokacje bez zmian. Testy: pełny 7167/7167 (z Oracle i Slow), bez AVX2 i bez
intrinsics 6975/6975, adaptery 51/51 (7 GDI+ pominiętych). ARM64 nieuruchomione. Notatki
`docs/releases/release-0.7.5.md`, `VERSION` 0.7.5.

## 0.8.0 — poprawki SPI i dalszy HDR

Zakres:
- Poprawki SPI zgłoszone przez wtyczkę HEIF — tylko na konkretne zgłoszenie. Kandydaci z tabeli luk (`ReportStoredSize`
  dla miniatur `thmb`, opcje dekodera per wtyczka) czekają, aż wtyczka wykaże potrzebę (pomiar zysku `thmb`, wybór
  elementu); bez zgłoszenia nie blokują 0.8.0. Każda zmiana jest addytywna.
- Gain map w enkoderach wtyczek: publiczny odczyt załącznika obrazu (`GainMapMetadata` + `GainMapImage`) i
  `GainMapMetadata.ToIso()`, z tą samą serializacją co zapis JPEG z 0.6.9.
- Generowanie gain map ISO z `HdrImage` i bazy SDR: port `generateGainMap` z libultrahdr 2.0.2 (Apache-2.0, notices).
- Zapis HDR do JPEG z mapą (baza z `ToneMap` albo podana).
- Konwersja Apple→ISO przy `Preserve`.
- Poza 0.8.0: przetwarzanie HDR w float, CICP PQ/HLG, AVIF.

DoD:
- Wyrocznia libultrahdr dla wygenerowanych map i plików.
- Budżet błędu round-trip `HdrImage` → JPEG → `ToHdr` ustalony przed implementacją.
- Wspólna bramka jakości.

## Wtyczka HEIF — osobne repozytorium

Wtyczka `PictureSharp.Heif` (HEIC: kontener HEIF, własny port HEVC intra w C#) powstaje w osobnym repozytorium
`../PictureSharp.Heif`, z własnym `VERSION` i SemVer od 0.1.0. Zakres, DoD, licencje (HM/kvazaar, wyrocznie
LGPL/GPL bez portowania) i patenty opisuje jej `plan.md`. Wtyczka zależy wyłącznie od paczki NuGet `PictureSharp`
i korzysta tylko z publicznego API; prace startują na 0.6.8.

Zależności od wydań rdzenia:

| Wersja wtyczki | Zakres | Minimalny PictureSharp |
| --- | --- | --- |
| 0.1.0–0.3.0 | Kontener i `Identify` (z `ContainerItemCount`), dekoder i enkoder HEVC intra, kolor, alfa, metadane | 0.6.9 |
| 0.4.0–0.5.0 | Gain map → `ToHdr`, `ReadMetadata` bez dekodu; SIMD i pomiary | 0.7.0 (SPI wtyczek); pełne `HdrInfo.Detected` od 0.7.1 |
| dalej | Zapis gain map w HEIC, ewentualnie `thmb` i opcje dekodera | 0.8.0 |

## Poza planem — kandydaci po 0.8.0

Do decyzji użytkownika, bez zobowiązań: przetwarzanie HDR w float, CICP PQ/HLG, AVIF (kontener wspólny z wtyczką
HEIF), proste formaty QOI, TGA i PNM.

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
- **0.7.1** — `ImageDecodeContext.AttachGainMap(…, detected)`: wtyczki zgłaszają wszystkie wykryte modele gain map; przegląd dokumentacji.
- **0.7.2** — dokumentacja skrócona o ~30%, `Mutate` a `Clone`, HEIC/HEIF przez wtyczkę `PictureSharp.Heif`; stabilny test alokacji. Bez zmian API.
- **0.7.3** — mniej alokacji w deflate/inflate PNG (pulowane bufory, ponownie używane tablice Huffmana) i przy metadanych PNG. Bez zmian API i bajtów wyjściowych.
- **0.7.4** — szybszy JPEG: dekodowanie ~2×, kodowanie ~3× na jednym wątku (szybka ścieżka Huffmana, IDCT AVX2 parami, wektorowy kolor, próbkowanie i kwantyzacja). Bez zmian API i wyników.
- **0.7.5** — szybszy WebP lossy (kodowanie −28…−32% na jednym wątku, dekodowanie −8…−11%) i potok wczytaj → orientacja → sRGB → resize → zapis (−40%, 106 MiB); naprawiony niedeterminizm wielowątkowego kodowania lossy. Bez zmian API i wyników.
