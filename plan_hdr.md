# PictureSharp 0.6.5 — obsługa JPEG HDR z iPhone’a

## 1. Cel i ustalony zakres

Dodać odczyt JPEG HDR z mapą wzmocnienia: ISO 21496-1 oraz starszy Apple HDR Gain Map. Po jawnym włączeniu (`DecoderOptions.LoadGainMap = true`) `Image.Load` dołącza mapę do obrazu, `image.ToHdr()` jawnie rekonstruuje liniowe piksele float, a `hdr.ToneMap()` daje jawną konwersję HDR→SDR. `Image.Load` i `Image.Identify` zachowują dotychczasową bazę SDR, identyczne piksele i brak nowych wyjątków — także gdy dane HDR w pliku są uszkodzone. Domyślnie `LoadGainMap = false`, więc bez zmiany opcji zachowanie i koszt są dokładnie takie jak w 0.6.4 (HDR jest opt-in).

Analiza `samples/iphone/` (zweryfikowana parserem segmentów 2026-10-16):

| Próbki JPEG | Obraz bazowy | Mapa | Headroom (liniowo) |
|---|---:|---:|---:|
| IMG_8112, IMG_8115, IMG_8116 | 1536×2048 | 768×1024 | 3,95 / 4,26 / 4,95 |
| IMG_8113 | 4032×3024 | 2016×1512 | 4,99 |
| IMG_8114 | 5712×4284 | 2856×2142 | 6,72 |

Ustalenia:

- Baza: ICC Display P3, EXIF z miniaturą (osobny JPEG wewnątrz APP1), APP10 `AROT`, MPF, w APP2 ISO tylko 4-bajtowy znacznik wersji. IMG_8113 ma orientację EXIF 6.
- MPF: wpis 1 typu „Baseline MP Primary” (`0x030000`), wpis 2 z typem **nieokreślonym (`0`)**. Typ MPF nie identyfikuje mapy — robią to dopiero segmenty pomocniczego JPEG.
- Mapa: jednokanałowy JPEG z XMP Apple (`HDRGainMapVersion` 131072, `HDRGainMapHeadroom`, `apdi:AuxiliaryImageType`), pełnymi metadanymi ISO (APP2, 61 bajtów), ICC „Display P3 Primaries; PQ (Adaptive Gain Curve)” i czasem własnym `AROT`.
- ISO: `minimum_version` = 0, mapa mono, `use_base_colour_space` = 1, bazowy headroom log2 = 0 (baza SDR), alternatywny log2 1,98–2,75, **ujemne `gain_map_min`** (mapa także przyciemnia), gamma 0,82–1,0, offsety 1e-5.
- Apple MakerNote: tagi 33 i 48 obecne; headroom wyliczony wzorem Apple z tych tagów zgadza się z alternatywnym headroom ISO i z `HDRGainMapHeadroom`. Te same pliki są więc czytelne dla obu modeli; ścieżkę Apple można testować na rzeczywistych próbkach przez wewnętrzne wymuszenie modelu, ale formuła i interpolacja Apple wymagają dodatkowo próbek syntetycznych bez ISO.

Poza wydaniem: dekoder HEIC/HEVC, enkoder HDR, generowanie map, HDR w `Mutate`, edycja pikseli `HdrImage`, wynik SDR w innej przestrzeni niż sRGB, GPU i wyświetlanie HDR. Pliki HEIC pozostają materiałem porównawczym dla narzędzi Apple.

Zachować pełne DoD obu wariantów. Wynajem Maca na 24 godziny następuje po przygotowaniu implementacji i narzędzi na Linuxie.

## 2. Publiczne API i zachowanie

Jedna ścieżka ładowania: `Image.Load` zwraca dotychczasową bazę SDR i — gdy włączono HDR (`LoadGainMap = true`) i plik zawiera obsługiwaną mapę — dołącza ją do obrazu. Rekonstrukcja HDR jest jawną, osobną operacją na załadowanym obrazie. Nie ma `LoadHdr`.

Docelowy sposób użycia (sprawdzany w `DocsExamplesTests` i `ConsumerCompilationTests`):

```csharp
using PictureSharp;
using PictureSharp.Processing;

var options = new DecoderOptions { LoadGainMap = true, ApplyExifOrientation = true };   // HDR jest opt-in
using Image image = Image.Load("IMG_8113.jpeg", options);
if (image.HasHdr)
{
    using HdrImage hdr = image.ToHdr();                 // liniowe RGB float, biel SDR = 1
    ReadOnlySpan<Rgba128> row = hdr.GetRowSpan(0);
    using Image sdr = hdr.ToneMap(new ToneMappingOptions { Exposure = -0.5f });
    sdr.SaveAsJpeg("tone-mapped.jpg");
}
image.SaveAsJpeg("base.jpg");                           // baza SDR, bez deklaracji HDR
```

Nazwy poniżej są robocze do zatwierdzenia migawki API, ale kształt (jedno `Load`, mapa dołączona do `Image`, jawne `ToHdr`, jedna metoda tone mappingu) jest ustalony.

### Dołączona mapa w `Image`

- **`Image.Load` / `LoadAsync`** — bez nowych przeciążeń i bez zmian pikseli bazy. Gdy JPEG zawiera mapę wybranego, obsługiwanego modelu, obraz przechowuje wewnętrzny, niezmienny załącznik: skompresowany strumień mapy (kopia, nie widok bufora wejściowego), parametry modelu, przestrzeń barwną bazy oraz geometrię zastosowaną przy ładowaniu (orientacja, `TargetSize`). Mapa nie jest dekodowana przy `Load`. Pamięć: 0,3–1,4 MB na próbkach, ok. 1,5% pikseli bazy.
- **Uszkodzone lub nieobsługiwane dane HDR nie zmieniają wyniku `Load`** — baza ładuje się jak dotąd, a stan opisuje `Image.Hdr`. Błąd ujawnia się dopiero w `ToHdr`.
- **`HdrInfo? Image.Hdr`** — ten sam niezmienny rekord co `ImageInfo.Hdr`; `null`, gdy HDR jest wyłączone (`LoadGainMap = false`, domyślnie), obraz nie pochodzi z pliku deklarującego mapę albo załącznik został usunięty. Pola: `HdrGainMapKinds Detected` (flagi: `Iso21496`, `Apple`, `UltraHdrXmp`; wzorem `MetadataKinds`), `HdrGainMapKinds Selected` (jedna flaga albo `None`), `Size GainMapSize`, `float? Headroom` (liniowo, wybranego modelu), `bool CanDecode`, `string? UnsupportedReason` (tekst diagnostyczny, nie do porównywania).
- **`bool Image.HasHdr`** — `true`, gdy `ToHdr` może zostać wywołane: załącznik jest obecny, model obsługiwany, a struktury sprawdzone przy `Load` poprawne. Skrót dla `Hdr is { CanDecode: true }`, nie gwarancja poprawności strumienia entropijnego mapy.
- **Ważność załącznika** — mapa opisuje dokładnie piksele bazy, więc:
  - każde `Mutate`, zastąpienie lub dodanie klatki i zmiana płótna usuwa załącznik (`Hdr` = `null`). Dotyczy także `AutoOrient` i `Resize`; obróconą lub pomniejszoną wersję HDR uzyskuje się przez `ApplyExifOrientation` i `TargetSize` przy `Load`;
  - `Clone` kopiuje załącznik (dzieli niezmienne bajty);
  - bezpośredni zapis przez `GetPixelSpan`/`CopyPixelDataFrom` nie jest wykrywany — `ToHdr` używa bieżących pikseli; opisane w XML docs;
  - zmiany `Metadata` (np. usunięcie ICC) nie wpływają na załącznik, bo przestrzeń barwna bazy jest w nim zapisana.
- **`DecoderOptions.LoadGainMap`** — `bool`, **domyślnie `false`** (decyzja użytkownika: HDR opt-in, zero kosztu i ryzyka dla obecnych użytkowników; nazwa pozytywna, bez podwójnego przeczenia). `true` włącza obsługę HDR w `Load`, `LoadAsync` i `Identify`: parsowanie MPF, XMP/APP2 HDR, MakerNote i nagłówków pomocniczego JPEG, kopiowanie skompresowanej mapy i rezerwację jej pikseli w `MaxPixels`. Przy `false` (domyślnie) wynik i koszt są takie jak w 0.6.4: `Image.Hdr` i `ImageInfo.Hdr` = `null`, `HasHdr` = `false`, a `ToHdr` zgłasza `InvalidOperationException` z komunikatem wskazującym `LoadGainMap = true`. W `Identify` opcja oznacza wykrywanie i opis HDR (bez kopiowania mapy). `EditMetadata` ją ignoruje, bo musi zachować cały kontener. Nazwa jest ogólna, bo mapy występują też w AVIF/HEIC/JPEG XL. Zmiana domyślnej wartości w przyszłości byłaby zmianą zachowania i wymaga opisu w notatkach wydania.
- **Istniejące opcje przy `Load`**:
  - `IgnoreMetadata` nie usuwa załącznika ani zapisanej w nim przestrzeni barwnej; obecny `ArgumentException` dla `ToSrgb` + `IgnoreMetadata` pozostaje bez zmian.
  - `ColorConversion.ToSrgb` konwertuje bazę jak dotąd (8-bit, przycięcie gamutu). `ToHdr` działa także wtedy: przechodzi do przestrzeni mnożenia macierzą, więc jedyną stratą jest gamut przycięty przy `Load`. Pełny gamut P3 wymaga `ColorConversion.None` — zalecenie w dokumentacji.
  - `TargetSize` zmniejsza bazę jak dotąd; `ToHdr` rekonstruuje przy rozmiarze zmniejszonej bazy, próbkując mapę według geometrii źródła.
  - Limity: `MaxPixels` przy `Load` rezerwuje piksele bazy i mapy, więc `ToHdr` nie przekroczy budżetu ustalonego przy ładowaniu; mapa nie zwiększa `MaxFrames`; metadane wymagane przez HDR podlegają `MaxMetadataBytes` także przy `IgnoreMetadata`. `Configuration` z `Load` jest zapamiętana i decyduje o dekoderze mapy.
  - Sekwencyjny czytnik klatek (`OpenFrameReaderAsync`) i `IcoFile` nie dołączają map.
- **Zapis** — enkodery nigdy nie zapisują mapy ani deklaracji HDR. Zachowanie HDR w pliku umożliwia wyłącznie `EditMetadata` bez rekompresji.

### Rekonstrukcja: `ToHdr` i `HdrImage`

- **`image.ToHdr(HdrOptions? options = null, CancellationToken cancellationToken = default)`** — rozszerzenie C# 14 w `PublicApi/Processing`, synchroniczne (praca CPU bez I/O, jak `Mutate`). Zwraca nowy `HdrImage` o rozmiarze `image.Size`, z kopią `image.Metadata`. Wyjątki odpowiadają stanowi `Image.Hdr`:
  - `Hdr` = `null` (brak mapy, usunięty załącznik albo domyślne `LoadGainMap = false`) → `InvalidOperationException` (stan obiektu, a nie format);
  - wykryty, poprawny, nieobsługiwany wariant → `NotSupportedException` z `UnsupportedReason`;
  - uszkodzona mapa lub metadane HDR → `ImageFormatException`;
  - po `Dispose` obrazu → `ObjectDisposedException`.
- **`HdrOptions`** (rekord w `PictureSharp`):
  - `float? TargetHeadroom` — **liniowy** stosunek do bieli SDR (jak `HdrImage.Headroom`, nie log2 jak w ISO); `null` = pełny zakres pliku; wartość skończona i ≥ 1; efektywny headroom = min(zadany, źródłowy). `TargetHeadroom = 1` daje zlinearyzowaną bazę (test tożsamości).
  - `HdrColorSpace? ColorSpace` — `null` = przestrzeń bieżących pikseli bazy (Display P3 albo sRGB po `ToSrgb`); `LinearSrgb` = rozszerzone liniowe sRGB.
- **`HdrImage : IDisposable`** (`PictureSharp`, model niezależny od kodeków i od życia źródłowego `Image`) — pojedynczy obraz, bez `Frames`. Składowe wzorowane na `Image`:
  - `Width`, `Height`, `Size`, `Bounds`;
  - `HdrColorSpace ColorSpace` (`LinearSrgb`, `LinearDisplayP3`; osobny plik enum), `float Headroom` — efektywny liniowy headroom (np. 4,99 ≈ 2,32 EV), `HdrGainMapKinds GainMapKind` — użyty model (jedna flaga);
  - `ImageMetadata Metadata` — kopia z obrazu; orientacja w `Metadata.Orientation`, gdy nie zastosowano `ApplyExifOrientation`;
  - piksele tylko do odczytu: indeksator `this[int x, int y]`, `ReadOnlySpan<Rgba128> GetRowSpan(int y)`, `GetPixelSpan()`, `ReadOnlyMemory<Rgba128> GetPixelMemory()`, `CopyPixelDataTo(Span<Rgba128>)`. Tylko odczyt chroni niezmienniki `Headroom`/`ColorSpace`; równoległe odczyty są bezpieczne.
- **`Rgba128`** (`PictureSharp`) — `[StructLayout(Sequential)]`, pola `float R, G, B, A`, prosta alfa, `IEquatable`, konwersje do/z `System.Numerics.Vector4`. JPEG daje `A = 1`. Wartości > 1 i ujemne składowe po konwersji gamutu pozostają nieobcięte; NaN i ±∞ nie występują w wyniku.
- **Własność pamięci** — `HdrImage` posiada własny bufor z `GC.AllocateUninitializedArray` (16 B/piksel, czterokrotnie więcej niż `Rgba32`). `Dispose` idempotentny; po nim składowe pikseli zgłaszają `ObjectDisposedException`. Wcześniej pobranych widoków nie można unieważnić, jak w `Image`; bufor nie wraca do puli. Zwolnienie źródłowego `Image` nie wpływa na wynik.

### Identyfikacja i tone mapping

- **`ImageInfo.Hdr`** — ten sam `HdrInfo?` co `Image.Hdr`, wyliczony z nagłówków bazy i mapy bez pikseli. Uszkodzone dane HDR dają `CanDecode = false` z przyczyną, a nie wyjątek. Wymaga `LoadGainMap = true`; `IgnoreMetadata` nie ukrywa `Hdr`.
- **`hdr.ToneMap(ToneMappingOptions? options = null, CancellationToken cancellationToken = default)`** — rozszerzenie C# 14 na `HdrImage` w `PublicApi/Processing`; zwraca nowy `Image` SDR (jedna klatka, `Rgba32`, sRGB, bez załącznika mapy). `ToneMappingOptions` (rekord w `PictureSharp.Processing`): `float Exposure` w EV, domyślnie 0, skończone; `float? WhitePoint` — liniowy, skończony, ≥ 1, domyślnie `hdr.Headroom`. Metadane wyniku: kopia `hdr.Metadata` bez ICC/opisu kolorów i bez rozpoznanych deklaracji HDR; orientacja zachowana, jeśli nie była zastosowana.

Tone mapping — kontrakt deterministyczny, nie obiecuje wyglądu aplikacji Zdjęcia:

1. Konwersja do liniowego sRGB (jeśli `ColorSpace = LinearDisplayP3`) tą samą macierzą co `ConvertToSrgb`.
2. Ekspozycja: RGB × 2^`Exposure`. `WhitePoint` W jest wyrażony w jednostkach po ekspozycji.
3. L = 0,2126 R + 0,7152 G + 0,0722 B. Dla L > 0: `Lout = L × (1 + L / W²) / (1 + L)`, RGB × (Lout / L); dla L ≤ 0: RGB = 0. W = 1 oznacza zwykłe przycięcie.
4. Przycięcie każdego kanału do [0, 1] (jawne gamut clipping), transfer sRGB, kwantyzacja `(byte)(v × 255 + 0,5)`. Alfa: `(byte)(A × 255 + 0,5)`.

Obliczenia w `float`, kolejność operacji ustalona w modelu double testów.

## 3. Implementacja — kolejność prac

### A. Korpus i referencje

- Dodać ignorowanie całego `samples/iphone/`. Prywatne zdjęcia i pochodne rastry pozostają lokalne; testy oznaczyć `Category=Local`.
- Przygotować inspektor zapisujący techniczny manifest próbek: hashe, wymiary, orientację, profile, wpisy MPF, parametry ISO i tagi MakerNote 33/48, bez GPS i bez innych pól EXIF.
- Zbudować syntetyczny korpus obu modeli: neutralne mapy, gradienty, ostre granice, ujemne i dodatnie log2 wzmocnienia, różne rozdzielczości (także nieparzyste i niecałkowite proporcje), mapy mono/RGB, sRGB/P3, `use_base_colour_space` 0/1, sam Apple bez ISO, sam ISO bez Apple oraz uszkodzone warianty.
- Przypiąć wyrocznię ISO do **libultrahdr v2.0.2, commit `e5f5a022fe96fc4dc2ee35c19f733a50df807abe`**, z wyłączonymi opcjonalnymi kodekami HEIF/AVIF. Narzędzie działa poza biblioteką; portowane fragmenty (Apache-2.0) otrzymują komentarze źródłowe i wpis w notices. [Wydanie referencyjne](https://github.com/google/libultrahdr/releases/tag/v2.0.2)

### B. Kontener JPEG i identyfikacja

- Dodać wspólny parser MPF, segmentów ISO APP2, XMP oraz tagów Apple MakerNote 33/48. Uwzględnić endianowość TIFF, offsety MPF liczone od nagłówka TIFF w segmencie MPF, długości, nakładanie zakresów, wpis bazy z offsetem 0 i przepełnienia. Offsety MakerNote Apple są względne wobec jego początku.
- Mapę wskazuje wpis MPF **oraz** deklaracja w samym pomocniczym JPEG: pełne metadane ISO w APP2 albo XMP `apdi:AuxiliaryImageType` / `HDRGainMapVersion`. Typ wpisu MPF (u Apple `0`) nie wystarcza; miniatura EXIF ani dowolny drugi JPEG nie stają się automatycznie mapą.
- Przy współistnieniu modeli wybierać ISO. Nie stosować wzoru Apple do mapy oznaczonej metadanymi ISO; uszkodzone ISO nie uruchamia cichego fallbacku Apple.
- Obsłużyć ISO z bazą SDR (bazowy headroom ≤ alternatywnego), mapą mono/RGB i przestrzeniami sRGB/P3. `minimum_version` > 0, odwrotny wariant z bazą HDR, inne przestrzenie niż sRGB/P3 i samodzielny Adobe/Ultra HDR XMP — wykrywane jako nieobsługiwane. Nieznany `writer_version` nie blokuje dekodowania.
- Zachować szybką ścieżkę zwykłego JPEG: przy domyślnym `LoadGainMap = false` żadnej dodatkowej pracy, a po włączeniu tylko przy obecności MPF. `Load` parsuje nagłówki mapy i kopiuje jej skompresowany strumień, ale jej nie dekoduje. Błędy w danych HDR nie mogą zmienić wyniku `Load`/`Identify` (regresja zgodności) — trafiają do `HdrInfo`. Rozszerzyć przekazywanie anulowania przez identyfikację, ponieważ obecne wywołania kończą przekazywanie tokenu po odczycie wejścia.

### C. Dekodowanie i rekonstrukcja

- Model: wewnętrzny, niezmienny `GainMapAttachment` w `Domain` (bajty mapy, deskryptor modelu, przestrzeń bazy, geometria, zapamiętane limity i konfiguracja) — bez zależności od kodeków. Usuwany w jednym miejscu agregatu przy `ProcessingState.ReplaceFrames`, zmianach kolekcji klatek i płótna; kopiowany przez `Clone`. Testy architektury pilnują, że `Domain` nie odwołuje się do dekodera JPEG.
- Dodać wewnętrzny port dekodowania mapy, usługę aplikacyjną `ToHdr` i rejestrację możliwości przy wbudowanym JPEG w `CodecRegistry`. Respektować `ImageConfiguration`: mapę dołącza wyłącznie wbudowany dekoder JPEG; gdy konfiguracja go nie zawiera (usunięty lub zastąpiony wtyczką), `Load` nie dołącza mapy (`Hdr` = `null`); nie ma ukrytego dekodera wbudowanego.
- Bazą jest bieżący raster `Rgba32` obrazu (zdekodowany przy `Load` istniejącym dekoderem); mapę dekoduje ten sam dekoder dopiero w `ToHdr`. Nie zmieniać jego algorytmów podczas integracji.
- ISO: interpretować parametry kanałów, gamma, offsety, headroom, wagę dla `TargetHeadroom` i przestrzeń wykonywania mnożenia (`use_base_colour_space`; przy 0 prymarne z ICC mapy). Próbki mapy są danymi liczbowymi; osadzony profil „PQ Adaptive Gain Curve” nie uruchamia zwykłej konwersji kolorystycznej mapy. Rekonstrukcję i interpolację oprzeć na przypiętej implementacji referencyjnej. [Kod referencyjny](https://github.com/google/libultrahdr/blob/v2.0.2/lib/src/gainmapmath.cpp)
- Starszy Apple: oddzielna rekonstrukcja z headroom wyliczanym z tagów MakerNote 33/48 według Apple, ograniczanym przez `TargetHeadroom`, odwrotnym transferem Rec.709 mapy i mnożnikiem `1 + (headroom − 1) × gain`. Interpolacja biliniowa zakodowanej mapy przed linearizacją — kolejność do potwierdzenia z ImageIO w oknie Apple. [Opis Apple](https://developer.apple.com/documentation/appkit/applying-apple-hdr-effect-to-your-photos)
- Linearizować bazę według przestrzeni zapisanej w załączniku (P3 albo sRGB po `ToSrgb`), przejść macierzą do przestrzeni mnożenia, odtworzyć HDR, następnie wykonać opcjonalną konwersję do `HdrOptions.ColorSpace`. Nie używać istniejącej konwersji kończącej się obcięciem do `Rgba32`.
- Geometria: `ToHdr` rekonstruuje przy bieżącym rozmiarze bazy. Przy `TargetSize` baza została zmniejszona przy `Load` jak dotąd (reduced IDCT + resize 8-bit), a mapa jest dekodowana z redukcją IDCT tylko do skali, przy której nie jest mniejsza niż potrzebna rozdzielczość, i próbkowana według geometrii źródła i zapamiętanej orientacji. Bez pełnowymiarowego bufora powiększonej mapy i bez resize float. Miniatura HDR różni się więc od pomniejszenia pełnej rekonstrukcji; budżet błędu dla tej ścieżki ustalić osobno.
- Rozdzielić rezerwowanie pikseli i klatek w budżecie: przy `Load` baza i mapa zużywają wspólny `MaxPixels` (gdy `LoadGainMap = true`), ale mapa nie zwiększa `MaxFrames`. Limity dotyczą wymiarów źródłowych również przy miniaturach. `ToHdr` sprawdza wymiary zdekodowanej mapy z nagłówkiem zapisanym przy `Load` (rozbieżność → `ImageFormatException`). Metadane wymagane przez HDR podlegają `MaxMetadataBytes` także przy `IgnoreMetadata`.
- Sprawdzać rozmiar bufora float przed alokacją. Przetwarzać wierszami z anulowaniem i zwracaniem buforów tymczasowych. Pierwsza wersja skalarna, bez nowych intrinsics.

### D. Metadane, CLI i dokumentacja

- Zastąpić obecne `NotSupportedException` dla MPF w `JpegMetadataCodec`/`EditMetadata` obsługą sprawdzonych kontenerów HDR (inne MPO nadal odrzucane). Zachować skompresowane dane obu obrazów; po zmianie segmentów bazy aktualizować rozmiar wpisu bazy, a offset mapy tylko o zmiany położone za segmentem MPF. Segmenty mapy pozostają nietknięte.
- Dopuścić edycję zwykłych metadanych, w tym usunięcie GPS, EXIF i XMP bazy, gdy nie są potrzebne wybranemu modelowi (dla ISO nie są). Przy edycji EXIF zachować MakerNote, jeśli wybranym modelem jest Apple. Zmianę lub usunięcie ICC bazy albo tagów niezbędnych wybranemu modelowi odrzucać atomowo przez `InvalidOperationException` z komunikatem wskazującym `Load` + `Save` (baza) lub `ToHdr` + `ToneMap` jako sposób uzyskania SDR; nie usuwać po cichu efektu HDR. Przypadek „wyczyść wszystkie metadane zdjęcia z iPhone’a” musi działać dla ISO bez wyjątku (oprócz ICC) i mieć test.
- Zwykłe kodowanie SDR i wynik tone mappingu usuwają rozpoznane deklaracje HDR (Apple XMP/MakerNote headroom pozostaje nieszkodliwy bez mapy, ale Ultra HDR `hdrgm`/`GContainer` w XMP bazy usuwać), odwołania do map i nieaktualne profile. Nie kopiować osieroconych danych HDR do PNG/WebP/JPEG.
- CLI: `info` pokazuje model i parametry HDR (`Image.Identify` z `LoadGainMap = true`); `convert` otrzymuje `-hdr base|tone-map` (konwencja jednego myślnika jak pozostałe opcje CLI), domyślnie `base` (bez parsowania HDR, `LoadGainMap = false`); `tone-map` ustawia `LoadGainMap = true`; oraz `-exposure` i `-white`. `-hdr tone-map` dla pliku bez obsługiwanego HDR kończy się błędem z przyczyną z `HdrInfo`.
- Uzupełnić dokumentację (wzorzec `LoadGainMap = true` → `Load` → `HasHdr` → `ToHdr` → `ToneMap`, reguły ważności załącznika po `Mutate`, `LoadGainMap`, zalecenie `ColorConversion.None` dla pełnego gamutu, zużycie pamięci, kontrakt tone mappingu), przykłady kompilowane, migawkę API i konsumenta trimming/Native AOT. Aktualizować `plan.md` i lokalny `progres.md`; `VERSION` dopiero przy przygotowaniu wydania.

## 4. Weryfikacja Apple w oknie 24 godzin

Przed wynajmem przygotować kompletny zestaw: skrypt Swift, korpus syntetyczny, wyniki PictureSharp, porównywarkę i instrukcję jednego uruchomienia. Wymagać macOS z API Adaptive HDR, narzędzi Swift i .NET 10; zapisać dokładne wersje środowiska.

Skrypt ma:

1. Odczytać bazę, mapę i metadane przez ImageIO.
2. Uzyskać HDR przez ImageIO/Core Image i zapisać liniowe RGBA float w określonej przestrzeni, bez zależności od monitora HDR. Apple udostępnia jawny odczyt HDR oraz map pomocniczych. [Opis API i przepływu](https://developer.apple.com/videos/play/wwdc2024/10177/)
3. Wygenerować referencje dla obu modeli (Apple na plikach bez ISO), pełnego i ograniczonego headroom oraz plików po edycji metadanych.
4. Porównać wyniki, zapisać raport błędów, wersje systemu, opcje i hashe wejść.
5. Wyeksportować referencje syntetyczne do regresji na Linuxie przed zakończeniem wynajmu.

Pierwszą część okna przeznaczyć na uruchomienie i wykrycie różnic, środkową na poprawki i powtórzenie, końcową na kompletność eksportu. Jeśli różnice pozostaną niewyjaśnione, nie zamykać DoD. IPhone służy dodatkowo do kontroli wizualnej i uzyskiwania kolejnych eksportów.

## 5. Testy i kryteria ukończenia

- **Matematyka:** niezależny model double; tolerancja rekonstrukcji i interpolacji na tych samych próbkach `1e-5 + 1e-5 × |reference|`; tone mapping maksymalnie 1 poziom kanału 8-bit. Neutralna mapa, headroom 1, `TargetHeadroom = 1`, czernie, L ≤ 0, światła > 1, wzmocnienia < 1 (ujemne log2), P3 poza gamutem sRGB, brak NaN/∞.
- **Wyrocznie:** oddzielne porównania dekodu JPEG, interpolacji i rekonstrukcji; nie przypisywać różnic dekodera JPEG formule HDR. Dla pełnego przepływu ustalić przed jego implementacją budżet błędu z niezależnych referencji i zapisać go w korpusie; nie rozszerzać tolerancji wyłącznie po to, by zaliczyć test. Na rzeczywistych próbkach: headroom z MakerNote zgodny z ISO i `HDRGainMapHeadroom`.
- **Geometria:** EXIF 1–8 z `ApplyExifOrientation` i bez, nieparzyste wymiary, różne skale mapy, reduced IDCT bazy i mapy, miniatury; zgodność rozmiarów z istniejącą polityką `TargetSize`.
- **Odporność:** błędne MPF, obcięcia, zera w mianownikach, nieznane wersje, sprzeczne deklaracje, limity, strumienie bez `Seek`, anulowanie podczas dekodu i rekonstrukcji, brak wycieków buforów. Te same uszkodzenia nie zmieniają pikseli `Load` ani wyniku `Identify` (poza `HdrInfo`) i ujawniają się jako właściwy wyjątek `ToHdr`.
- **API:** stan i wyjątki `ToHdr` (brak mapy, domyślne opcje, po `Mutate` → `InvalidOperationException`; nieobsługiwany wariant → `NotSupportedException`; uszkodzona mapa → `ImageFormatException`; po `Dispose`), `Clone` zachowuje mapę, `ToHdr` po `ToSrgb` przy `Load`, niezależność `HdrImage` od życia `Image`, `Dispose` i `ObjectDisposedException`, walidacja opcji (`TargetHeadroom`, `Exposure`, `WhitePoint`), konfiguracja bez wbudowanego JPEG, `ConsumerCompilationTests`, przykład z dokumentacji.
- **`LoadGainMap`:** domyślna wartość `false`; z domyślnymi opcjami piksele, `ImageInfo` i alokacje identyczne jak w 0.6.4 (test alokacji na próbce z MPF), `Hdr` = `null`. Z `true` — `Hdr` wypełnione.
- **Regresje:** zwykłe `Load` i dotychczasowy korpus SDR bez zmian pikseli i alokacji (pliki bez MPF); `EditMetadata` zachowuje skompresowane dane i odczytywalność HDR (także przez libultrahdr); zapis SDR nie deklaruje HDR.
- **Wydajność:** małe obrazy, rzeczywiste próbki 3/12/24 MP i syntetyczne 48 MP; czas, alokacje, szczytowa pamięć, miniatury i anulowanie; narzut `Load`/`Identify` z MPF przy `LoadGainMap = true` względem domyślnego; osobno `ToHdr` i `ToneMap`. Potwierdzona regresja zwykłego JPEG > 5% wymaga usunięcia przed wydaniem.
- **Bramka wydania:** Release bez ostrzeżeń, pełne testy i Oracle, architektura, API, konsument trim/Native AOT oraz osobny przebieg ARM64. Zapisane referencje Apple muszą przechodzić na Linuxie; brak Maca lub pominięty Oracle nie oznacza zaliczenia zgodności.

0.6.5 jest ukończone dopiero po rekonstrukcji obu modeli, potwierdzeniu dostarczonych JPEG, zaliczeniu weryfikacji Apple i całej bramki jakości.

## 6. Stan implementacji (2026-10-16)

Zrobione: model i API z sekcji 2 (`LoadGainMap`, `Image.Hdr`/`HasHdr`, `ToHdr`, `HdrImage`, `Rgba128`, `HdrOptions`, `ToneMap`, `ToneMappingOptions`, `ImageInfo.Hdr`), parser MPF/ISO/XMP/MakerNote, rekonstrukcja ISO (IDW, gamma, offsety, waga headroom, `use_base_colour_space` 0/1, mapy mono/RGB) i Apple, geometria (orientacja, `TargetSize` z reduced IDCT mapy), usuwanie załącznika przy `Mutate`/zmianach klatek, `EditMetadata` z relokacją MPF i ochroną ICC/MakerNote, CLI, `docs/hdr.md`, migawka API, scenariusz HDR w `AotSmoke` (trim + Native AOT), korpus `tests/assets/corpus/hdr` z modelem double (`hdr_reference.py`), testy w `tests/PictureSharp.Tests/Hdr/`.

Wyrocznia libultrahdr v2.0.2 (2026-10-16, `scripts/build-ultrahdr-reference.sh`, commit zweryfikowany): porównanie źródeł wykazało i usunięto trzy rozbieżności — odległości IDW liczone od przyciętych sąsiadów, jeden współczynnik skali (szerokość) przy proporcjach mapy różnych o ≤ 1% oraz walidację metadanych (ujemne offsety, bazowy headroom < 0, min > max). Układ binarny ISO, `applyGain` i waga headroom były zgodne. Wyniki (`HdrUltraHdrOracleTests`, Category=Oracle): funkcje dokładne libultrahdr na identycznych pikselach — w tolerancji 1e-5 + 1e-5·|ref| na całym korpusie ISO i 1e-4 względnie na zdjęciach iPhone (krzywa ICC Apple vs dokładne sRGB); domyślne LUT libultrahdr — maks. 0,0044; pełny `ultrahdr_app` — maks. 0,016, mediana 0,42%, p99 2,9% (budżet w `hdr_reference.json`: 0,025 / 0,6% / 4%, ustalony po pierwszym pomiarze). Na zdjęciach iPhone (baza 4:2:0) pełny przepływ różni się bardziej (mediana 0,7%, p99 16,7%), bo libultrahdr pobiera chrominancję bez wygładzania; to różnica dekodera JPEG, nie formuły. Odstępstwo: mapy o proporcjach różnych o > 1% libultrahdr przeskalowuje do rozmiaru bazy, PictureSharp używa osobnych współczynników osi (bez pełnowymiarowej mapy).

Otwarte przed zamknięciem DoD:
- Weryfikacja Apple na Macu (sekcja 4): skrypt Swift, referencje ImageIO, kolejność interpolacji/linearizacji w modelu Apple.
- Manifest techniczny próbek (inspektor bez GPS).
- Usuwanie deklaracji Ultra HDR (`hdrgm`/`GContainer`) z XMP bazy przy zapisie SDR.
- Benchmarki BenchmarkDotNet (3/12/24/48 MP, alokacje, szczytowa pamięć) i przebieg ARM64; tone mapping jest jednowątkowy.
