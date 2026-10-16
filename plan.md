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
- 0.6.6: decyzja użytkownika (2026-10-16) — testy na macOS odłożone do dostępności sprzętu; etap pozostaje otwarty.
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
  `GainMapQuality` z domyślną wartością ustaloną według libultrahdr. Nazwy robocze do migawki API.

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
- Enkoder JPEG z mapą (Ultra HDR 1.1 / ISO 21496-1, jak libultrahdr): baza z ICC, EXIF i XMP; APP2 MPF z dwoma
  obrazami i poprawnymi offsetami (wspólny kod z `JpegMpf` i `EditMetadata`); w bazie XMP `hdrgm`/GContainer i APP2
  z wersją ISO; w mapie APP2 z metadanymi ISO i XMP `hdrgm`. Mapa nieprzekształcona jest kopiowana bez rekompresji;
  przekształcona kodowana jako JPEG mono lub RGB jak źródło.
- Warianty: źródło ISO oraz Apple+ISO zapisywane jako ISO (bez Apple MakerNote w nowej deklaracji); samo Apple
  (iOS 14–17) → `Require` daje `NotSupportedException`, `Preserve` zapisuje SDR. Konwersja Apple→ISO poza zakresem.
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
| Liczba elementów kontenera w Identify | `ImageInfo` ma tylko `FrameCount`; to liczba klatek, nie elementów HEIF | DoD wtyczki 0.1.0: liczba elementów bez traktowania kafli/alfy/metadanych jako animacji; rozważyć opcjonalne `ContainerItemCount` | 0.7.0; nazwa i semantyka do decyzji |
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

Wtyczka `PictureSharp.Heif` (HEIC: kontener HEIF, własny port HEVC intra w C#) powstaje w osobnym repozytorium
`../PictureSharp.Heif`, z własnym `VERSION` i SemVer od 0.1.0. Zakres, DoD, licencje (HM/kvazaar, wyrocznie
LGPL/GPL bez portowania) i patenty opisuje jej `plan.md`. Wtyczka zależy wyłącznie od paczki NuGet `PictureSharp`
i korzysta tylko z publicznego API; prace startują na 0.6.8.

Zależności od wydań rdzenia:

| Wersja wtyczki | Zakres | Minimalny PictureSharp |
| --- | --- | --- |
| 0.1.0–0.3.0 | Kontener i `Identify`, dekoder i enkoder HEVC intra, kolor, alfa, metadane | 0.6.8 |
| 0.4.0–0.5.0 | Gain map → `ToHdr`, `ReadMetadata` bez dekodu; SIMD i pomiary | 0.7.0 (SPI wtyczek) |
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
