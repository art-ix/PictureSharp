# PictureSharp: plan rozwoju do 0.8.0

## Stan i cel

**Bieżąca wersja: 0.6.8 (opublikowana).** PictureSharp to biblioteka obrazów dla .NET 10+ w C# 14, bez zależności i
kodu natywnego: WebP, PNG/APNG, JPEG, GIF, BMP, PCX i ICO, animacje (także sekwencyjnie), metadane, zarządzanie
kolorem, operacje graficzne, adaptery (System.Drawing, SkiaSharp, ASP.NET Core) i odczyt HDR z JPEG z gain map.

Dalej: zachowanie HDR przy edycji i zapis JPEG z gain map (0.6.9), stabilizacja API i SPI wtyczek dla HDR i metadanych
(0.7.0), poprawki SPI zgłoszone przez wtyczkę HEIF i generowanie gain map (0.8.0). Wtyczka HEIC `PictureSharp.Heif`
(własny port HEVC intra w C#) powstaje równolegle w osobnym repozytorium, z własnym SemVer od 0.1.0. Weryfikacja
Apple na macOS (0.6.6) czeka na dostęp do sprzętu. Pozostałe nowe formaty (CUR, TIFF, QOI, TGA, PNM, AVIF, JPEG XL)
są poza planem.

Decyzje użytkownika (2026-10-16): kolejność rdzenia 0.6.9 → 0.7.0 → 0.8.0; HEIF jako wtyczka z portem dekodera i
enkodera HEVC intra w C#, nie jako wrapper natywnego libheif; wtyczka w osobnym repozytorium z własnym SemVer; rdzeń
0.8.0 = poprawki SPI zgłoszone przez wtyczkę + dalszy HDR.

Istniejące API i ograniczenia: `README.md`, `docs/`, XML docs, `tests/assets/PublicApi.approved.txt`. Pomiary:
`docs/performance.md`. Ukończone wydania: [changelog](#changelog), `docs/releases/` i historia Git. Lokalna historia
pracy: `progres.md` (bez commitowania).

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

## Kolejność wydań

| Wersja | Zakres | Status |
| --- | --- | --- |
| 0.6.6 | Weryfikacja HDR z ImageIO/Core Image na macOS, usuwanie deklaracji Ultra HDR przy zapisie SDR | Odłożona do dostępności macOS |
| 0.6.9 | HDR przy edycji: gain map w operacjach geometrycznych i zapis JPEG z mapą ISO 21496-1 | Planowana |
| 0.7.0 | Integracja wszystkich obszarów, SPI wtyczek dla gain map i metadanych, stabilizacja API i wydanie | Planowana |
| 0.8.0 | Poprawki SPI zgłoszone przez wtyczkę HEIF, gain map w enkoderach wtyczek, generowanie gain map i zapis HDR | Planowana |

Wtyczka `PictureSharp.Heif` ma osobne repozytorium i własne wersje (od 0.1.0); jej zależności od wydań rdzenia
opisuje sekcja [Wtyczka HEIF](#wtyczka-heif--osobne-repozytorium).

Numery określają kolejność, nie terminy. Każde wydanie wymaga własnego DoD oraz wspólnej bramki jakości. Nazwy nowych opcji i metod poniżej są robocze do czasu zatwierdzenia migawki API.

## Zaległe weryfikacje — przed 0.6.9

Krótkie zadania, które nie dodają funkcjonalności, ale blokują zamknięcie bramki 0.7.0:
- ARM64 końcowej wersji 0.6.8 (testy, `*SimdTests`, fallbacki). Orange Pi jest wyłączony poleceniem użytkownika; bez
  jego zgody punkt przechodzi do bramki 0.7.0 i pozostaje otwarty.
- NuGet: obecność 0.6.8 wszystkich czterech paczek w indeksie; Trusted Publishing dla `PictureSharp.Drawing`,
  `PictureSharp.Skia` i `PictureSharp.AspNetCore`.
- 0.6.6: decyzja użytkownika, czy czekamy na macOS, czy zamykamy etap z adnotacją. Bez porównania z ImageIO/Core Image
  wariant Apple pozostaje „zgodny z referencjami Linux, niepotwierdzony na Apple” i nie jest oznaczany jako zweryfikowany.

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

Decyzje do zatwierdzenia przed implementacją (nazwy robocze):
- Operacje geometryczne zachowują mapę zamiast ją usuwać. To zmiana zachowania `image.Hdr`/`HasHdr` po `Mutate`
  (wcześniej `null`, `ToHdr` → `InvalidOperationException`); opisać w notatkach. Alternatywa: jawna opcja w `Mutate`.
- `JpegEncoderOptions.GainMap` typu `JpegGainMapMode`: `None` (domyślnie), `Preserve` (zapis mapy, gdy dołączona i
  dekodowalna, inaczej zwykły JPEG) i `Require` (brak mapy lub wariant niezapisywalny → wyjątek). Dodatkowo
  `GainMapQuality` z domyślną wartością ustaloną według libultrahdr.

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
- Enkoder JPEG z mapą (Ultra HDR 1.1 / ISO 21496-1, jak libultrahdr): baza z ICC, EXIF i XMP; APP2 MPF z dwoma
  obrazami i poprawnymi offsetami (wspólny kod z `JpegMpf` i `EditMetadata`); w bazie XMP `hdrgm`/GContainer i APP2
  z wersją ISO; w mapie APP2 z metadanymi ISO i XMP `hdrgm`. Mapa nieprzekształcona jest kopiowana bez rekompresji;
  przekształcona kodowana jako JPEG mono lub RGB jak źródło.
- Warianty: źródło ISO oraz Apple+ISO zapisywane jako ISO (bez Apple MakerNote w nowej deklaracji); samo Apple
  (iOS 14–17) → `Require` daje `NotSupportedException`, `Preserve` zapisuje SDR. Konwersja Apple→ISO poza zakresem.
- Zapis PNG/WebP/innych formatów pozostaje SDR; `EditMetadata` bez zmian. Limity rozmiaru kodowanej mapy i anulowanie
  w resamplingu i kodowaniu mapy.
- CLI: `convert`/`resize` z `-hdr keep` (JPEG→JPEG z mapą); `info` pokazuje mapę po przekształceniu.

Referencje (przed implementacją, rozszerzenie `tests/assets/hdr_reference.py`, `python3 -I`):
- Model numpy przekształceń mapy: flip/rotate/crop wyrównany do skali mapy bit-exact; resize i crop niewyrównany ≤ 1 na
  kanał 8-bit względem modelu i efektów libultrahdr.
- Rekonstrukcja po edycji porównana z `ToHdr` oryginału przekształconym tą samą geometrią w float; tolerancja ustalona i
  zapisana przed implementacją.
- libultrahdr `ultrahdr_app` dekoduje nasze pliki: metadane identyczne, rekonstrukcja w budżetach z 0.6.5.

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

## 0.7.0 — integracja i stabilizacja

Zakres:
- Ukończyć DoD obsługi ICO, wszystkich sześciu obszarów, adapterów z 0.6.4, JPEG HDR z 0.6.5 i 0.6.9. Ujednolicić opcje, nazwy, własność buforów, limity, anulowanie i polityki metadanych/koloru.
- SPI wtyczek dla gain map i metadanych (szczegóły w podsekcji poniżej); wbudowane kodeki pozostają na kontraktach
  wewnętrznych.
- Testy end-to-end: tworzenie i odczyt wielowariantowego ICO; miniatura JPEG z orientacją i konwersją ICC; sekwencyjna konwersja animacji z maską i resize; edycja metadanych bez rekompresji; przetwarzanie przy ograniczonych zasobach; odczyt JPEG HDR i jawna konwersja do SDR; JPEG HDR → resize → zapis z mapą → odczyt przez libultrahdr; wtyczka testowa korzystająca z nowego SPI HDR.
- Uzupełnić CLI i przewodniki o ICO, miniatury, limity, sekwencyjne animacje, kolor, metadane, HDR i nowe operacje. Przykłady sprawdzane przez `DocsExamplesTests`.
- Rozszerzyć istniejącą macierz wydajności o nowe ścieżki, czas anulowania, szczytową pamięć i długie animacje; opisać kompromisy jakości i ograniczenia.
- Sprawdzić pakiet NuGet, trimming/NativeAOT w obsługiwanym zakresie i użycie z lokalnego feedu na x64/ARM64. Przygotować notatki migracyjne z 0.5.0, w tym zmianę nazwy pakietu i przestrzeni nazw z RasterSharp na PictureSharp.
- Rozstrzygnąć zaległe weryfikacje: ARM64 (wymaga zgody użytkownika na Orange Pi) i status 0.6.6.

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
| Kontrakt orientacji kontenera | nieopisany | `irot`/`imir` bez podwójnego obrotu z EXIF | 0.7.0 (dokumentacja) |
| Gain map i serializacja ISO w enkoderze | internal | zapis HEIC z gain map | 0.8.0 |
| Rozmiar źródłowy przy mniejszym wyniku | `DecodedImage.StoredSize` internal | miniatura z elementu `thmb` | 0.8.0, jeśli wtyczka wykaże zysk |
| Opcje dekodera per wtyczka | `DecoderOptions.Ico/Png/WebP` zamknięte | np. wybór elementu | 0.8.0, jeśli potrzebne |
| CICP PQ/HLG | brak | HDR10 HEIC z Androida | poza 0.8.0 |

Zakres (nazwy robocze, do zatwierdzenia przed implementacją):
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

DoD SPI:
- Wtyczka testowa w `tests/PictureSharp.Tests` (kontener z bazą JPEG/PNG, mapą JPEG i payloadem ISO) daje `ToHdr`
  bitowo równe ścieżce JPEG dla tych samych danych.
- Testy `TargetSize`, orientacji, `ColorConversion`, limitów, uszkodzonych deklaracji oraz `ReadMetadata`/`EditMetadata`
  przez wtyczkę.
- Zwykły JPEG HDR bez zmian bajtów i pikseli (`hdr_reference`, wyrocznia libultrahdr).
- Testy architektury, `ConsumerCompilationTests`, migawka API z samymi dodatkami, `verify-package-consumer.sh` z
  wtyczką używającą nowego SPI.

DoD: wszystkie wcześniejsze kryteria zamknięte, pełna wspólna bramka jakości zaliczona, dokumentacja i paczka gotowe. Niezrealizowanych punktów nie oznaczamy jako ukończone; zmiana zakresu wymaga jawnej decyzji i aktualizacji planu.

## 0.8.0 — poprawki SPI i dalszy HDR

Zakres:
- Poprawki SPI zgłoszone przez wtyczkę HEIF. Kandydaci z tabeli luk to `ReportStoredSize` dla miniatur `thmb` i opcje
  dekodera per wtyczka. Każda zmiana jest addytywna i potwierdzona potrzebą wtyczki.
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

Wtyczka `PictureSharp.Heif` powstaje w osobnym repozytorium git, z własnym `VERSION` i SemVer od 0.1.0. Zależy wyłącznie
od paczki NuGet `PictureSharp` w minimalnej wersji podanej niżej i korzysta tylko z publicznego API (test architektury,
CI na paczce z NuGet lub lokalnego feedu). Prace startują od razu, na 0.6.8; luki SPI zamyka rdzeń według tabeli w 0.7.0.
Zakres poniżej przenosimy do `plan.md` nowego repozytorium przy jego założeniu.

| Wersja wtyczki | Zakres | Minimalny PictureSharp |
| --- | --- | --- |
| 0.1.0 | Repo, CI, kontener HEIF (ISOBMFF), detekcja, `Identify`, limity, fuzzing parsera | 0.6.8 |
| 0.2.0 | Dekoder HEVC intra (skalar), siatka, kolor, alfa, `irot`/`imir`/`clap`, ICC/EXIF/XMP | 0.6.8 |
| 0.3.0 | Enkoder HEVC intra Main 8-bit 4:2:0, alfa, siatka, metadane | 0.6.8 |
| 0.4.0 | Gain map Apple/ISO → `ToHdr`, `ReadMetadata` bez dekodu | 0.7.0 |
| 0.5.0 | SIMD i równoległość, pomiary względem libheif, ARM64 | 0.7.0 |
| dalej | Zapis gain map w HEIC, ewentualnie `thmb` i opcje dekodera | 0.8.0 |

Cel: moduł `IImageFormatModule` (rejestracja przez `ImageConfigurationBuilder.AddFormat`), który odczytuje i zapisuje
HEIC (HEVC w kontenerze HEIF) bez kodu natywnego. Zdjęcia HEIC zawierają tylko obrazy intra, więc port obejmuje HEVC
intra, bez kompensacji ruchu.

Zasady:
- Te same reguły co rdzeń: `net10.0`, C# 14, BCL-only, bez `unsafe`, P/Invoke i `System.IO.Compression`; zależność
  wyłącznie od `PictureSharp` przez publiczne API (test architektury jak dla adapterów). Wyjątki, limity, anulowanie,
  pule buforów, `Rgba32` z prostą alfą.
- Licencje: port tylko z kodu zgodnego z MIT. Dekoder HEVC według ITU-T H.265 (bezpłatna specyfikacja) i
  oprogramowania referencyjnego HM (BSD-3); enkoder według HM i kvazaar (BSD-3). Kontener według ISO/IEC 23008-12 i
  ISO/IEC 14496-12. libde265, libheif (LGPL) i x265 (GPL) wyłącznie jako wyrocznie, bez portowania kodu.
  `THIRD-PARTY-NOTICES.txt` paczki uzupełniony o porty.
- Patenty HEVC: biblioteka ich nie rozstrzyga. README paczki opisuje sytuację; decyzja o publikacji paczki należy do
  użytkownika i zapada przed wydaniem.
- Źródła referencyjne przypięte w `reference/` repozytorium wtyczki (HM, kvazaar, libheif + libde265 do narzędzi), read-only; pobranie
  wymaga zgody użytkownika. Narzędzia wyroczni (`heif-dec`, `heif-enc`, `heif-info`, dekoder HM) budowane skryptem
  `scripts/build-heif-reference.sh`, z nadpisaniem katalogu przez `PICTURESHARP_HEIF_TOOLS`.
- Port funkcja po funkcji z komentarzem źródłowym; najpierw skalar, SIMD dopiero po pomiarach i z testami identyczności.

Zakres dekodowania:
- Kontener: `ftyp` (`heic`, `heix`, `mif1`), `meta` z `hdlr pict`, `pitm`, `iinf`/`infe`, `iloc` (wersje 0–2, metody
  0 i 1), `iref` (`dimg`, `auxl`, `thmb`, `cdsc`, `prem`), `iprp`/`ipco`/`ipma` (`hvcC`, `ispe`, `colr` nclx/prof,
  `pixi`, `irot`, `imir`, `clap`, `auxC`), `idat`. Elementy `grid` (kafle iPhone’a) i `iden`; transformacje w kolejności
  z `ipma`. Ustalić relację z `ApplyExifOrientation` (HEIF stosuje `irot`/`imir`, EXIF orientation jest informacyjne).
- HEVC: profile Main, Main Still Picture i Main 10; chroma 4:2:0 i 4:0:0. VPS/SPS/PPS, segmenty slice, CABAC, drzewo
  kodowania, predykcja intra (35 trybów), transform skip, listy skalowania, transformaty 4–32 i DST 4×4, dekwantyzacja,
  deblocking, SAO, kafle i WPP, PCM. Slice inter, 4:2:2/4:4:4 (RExt) i SCC → `NotSupportedException`.
- Kolor: YCbCr → RGB według nclx (macierze BT.601/709/2020, zakres pełny/ograniczony) z dokładnie opisanym
  zaokrągleniem; ICC do metadanych; 10-bit → 8-bit jak PNG 16-bit. Alfa z obrazu pomocniczego (auxC alpha) jako prosta
  alfa, z odwróceniem premultiplikacji przy `prem`.
- Metadane: elementy Exif (z prefiksem offsetu), XMP i ICC w `ImageMetadata`; `Identify` bez dekodowania HEVC; miniatura
  `thmb` jako możliwa szybka ścieżka `TargetSize` (wymaga SPI z rdzenia 0.8.0).
- HDR: gain map Apple (`urn:com:apple:photo:2020:aux:hdrgainmap`) i ISO (`tmap`) dołączane przez SPI z rdzenia 0.7.0, więc
  `ToHdr`/`ToneMap` działają dla HEIC. Mapy głębi i matowe portretów, Live Photos, sekwencje HEIF i AVIF poza zakresem
  (AVIF może później współdzielić parser kontenera).
- Limity: `MaxInputBytes`, `MaxPixels` (suma kafli, alfy i mapy), `MaxMetadataBytes`, liczba elementów, głębokość i
  cykle `iref`, wymiary siatki i kafli, rozmiary NAL. Kafle siatki dekodowane równolegle, wynik niezależny od liczby wątków.

Zakres kodowania:
- Pojedynczy obraz (polityka dla animacji jak w pozostałych enkoderach jednoobrazowych), HEVC Main 8-bit 4:2:0, alfa
  jako obraz pomocniczy 4:0:0, siatka kafli dla dużych obrazów, nclx/ICC, Exif i XMP. Domyślne parametry kolorów
  ustalone według `heif-enc`.
- Enkoder intra: `Quality` 0–100 → QP, CTU 32/64, wybór trybów przez SATD i uproszczone RDO, CABAC, deblocking i SAO;
  deterministyczny i niezależny od liczby wątków (kafle równolegle).
- `HeifEncoderOptions` (robocze): `Quality`, `TileSize`, `Effort`, `MaxDegreeOfParallelism`. Bezstratny tryb, 4:4:4 i
  10-bit poza pierwszymi wersjami. Zapis gain map w HEIC po SPI z rdzenia 0.8.0 (decyzja po etapie dekodera).

Referencje i tolerancje (ustalone przed implementacją):
- Dekoder: YCbCr bit-exact (tolerancja 0) z HM i libde265 na strumieniach konformacyjnych intra JCT-VC (pobranie za
  zgodą) i syntetycznym korpusie z `heif-enc` (x265 i kvazaar) oraz ffmpeg. RGB z `heif-dec` przy tej samej konwersji
  kolorów: 0, inaczej ustalona tolerancja.
- Korpus syntetyczny: siatki, alfa, rotacje/lustra/clap, 10-bit, monochrom, nclx/ICC, Exif/XMP, gain map Apple i ISO;
  uszkodzone, obcięte, cykliczne referencje i fuzzing. Prywatne zdjęcia HEIC z iPhone’a tylko lokalnie (`Category=Local`).
- Enkoder: pliki odczytywane przez libheif i dekoder HM; nasz dekoder identyczny z HM na naszych plikach. Kryterium
  jakości/rozmiaru względem x265 i kvazaar przy równym PSNR/SSIM (np. rozmiar ≤ 1,5× x265 `medium`) zapisane przed
  implementacją enkodera.
- Zgodność odbiorców: ręcznie Windows (HEIF Image Extensions) i Apple Photos, gdy dostępne; brak nie jest zaliczeniem.

Kolejność według tabeli wersji wtyczki; każdy etap z testami i wpisem w dzienniku postępu repozytorium wtyczki, a
publikacja każdej wersji wyłącznie na decyzję użytkownika. SIMD i równoległość (IDCT/DST, predykcja intra, deblocking,
SAO, YCbCr→RGB) dopiero po wersji skalarnej.

DoD:
- Wyrocznie zielone w ustalonych tolerancjach; skalar i SIMD identyczne na x64 i ARM64; wynik niezależny od liczby wątków.
- Testy limitów, uszkodzonych danych i anulowania; żadne wejście nie daje niekontrolowanego wyjątku (fuzzing `Category=Slow`).
- Pomiary dekodowania 12/48 MP i kodowania względem libheif (czas, alokacje, szczytowa pamięć).
- Paczka `PictureSharp.Heif` z README (w tym nota o patentach), `LICENSE` i notices; test architektury; konsument
  trim/Native AOT; dokumentacja z przykładami sprawdzanymi testami w repozytorium wtyczki; przykład CLI z rejestracją wtyczki.

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
