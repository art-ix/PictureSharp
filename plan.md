# PictureSharp: plan rozwoju do 0.7.0

## Stan i cel

**Bieżąca wersja: 0.5.4. Fazy 0–10, obsługa ICO, kontrola zasobów i anulowanie oraz miniatury JPEG ukończone; 0.5.2 zmieniło nazwę biblioteki z RasterSharp na PictureSharp.** Dalsze wydania poniżej są planowane; ich zakres nie oznacza istniejącej funkcjonalności.

PictureSharp to biblioteka obrazów dla .NET 10+, napisana w C# 14, bez zależności biblioteki i kodu natywnego. Odczytuje i zapisuje WebP, PNG/APNG, JPEG, GIF, BMP i PCX; wydanie 0.5.1 dodało ICO. Udostępnia animacje, metadane, operacje na pikselach, skalowanie, geometrię, korekcję kolorów i filtry przez `Image.Load` / `Mutate` / `Clone` / `SaveAs*`.

Do 0.7.0 dodajemy odczyt i zapis ICO oraz rozwijamy sześć obszarów: miniatury przy dekodowaniu, kontrolę zasobów, sekwencyjne animacje, zarządzanie kolorem, metadane i operacje graficzne. Pozostałe nowe formaty (CUR, TIFF, QOI, TGA, PNM, AVIF, JPEG XL) pozostają poza tym planem.

Szczegóły istniejącego API i ograniczeń: `README.md`, `docs/`, XML docs oraz `tests/assets/PublicApi.approved.txt`. Wyniki pomiarów: `docs/performance.md`. Lokalna historia pracy: `progres.md` (bez commitowania). Historyczne szczegóły implementacji pozostają w historii Git.

## Zasady obowiązujące we wszystkich wydaniach

- `net10.0`, `LangVersion=14.0`, nullable, warnings as errors i XML docs. Publiczne API w przestrzeni `PictureSharp`; kodeki i szczegóły implementacji są `internal`.
- Biblioteka używa wyłącznie BCL: bez `PackageReference`, P/Invoke, `unsafe` oraz `System.IO.Compression`. Deflate/inflate pochodzi z `Compression/`. `System.Drawing.Primitives` jest dozwolone.
- `Image` i `ImageFrame` przechowują `Rgba32` z prostą alfą. Bufory robocze z `ArrayPool<T>`, piksele z `GC.AllocateUninitializedArray`; gorące pętle bez alokacji.
- Zachowujemy dotychczasowe wyniki i domyślne zachowanie. Nowe przekształcenia obrazu wymagają jawnej opcji. Ewentualne nowe domyślne limity zasobów muszą być opisane jako zmiana zachowania.
- Uszkodzone dane: `ImageFormatException`; poprawne, nieobsługiwane warianty: `NotSupportedException`. Anulowanie: `OperationCanceledException`. Błędy I/O zachowują właściwy typ. Żadne złośliwe wejście nie może powodować niekontrolowanych wyjątków indeksowania lub przepełnienia.
- Nowe porty WebP bazują na read-only `reference/libwebp-1.6.0/`, z komentarzami źródłowymi i aktualizacją `THIRD-PARTY-NOTICES.txt`. Zachowujemy semantykę C, w tym kolejność operacji `float` i 23-bitowe koszty VP8L.
- Najpierw poprawna wersja skalarna. SIMD x64/ARM64 i równoległość dodajemy po pomiarach; wyniki muszą być identyczne ze skalarem i niezależne od liczby wątków.
- Testy, benchmarki i wyrocznie mogą używać zewnętrznych pakietów. Pobieranie zewnętrznych korpusów wymaga zgody użytkownika. Prywatne zdjęcia pozostają lokalne.
- Commit, push i publikacja wyłącznie na polecenie użytkownika. `VERSION` zmieniamy przy przygotowaniu wydania, nie przy rozpoczęciu etapu.

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
| 0.6.0 | Sekwencyjny odczyt i zapis animacji (3) | Planowana — następny etap |
| 0.6.1 | Typowane metadane i edycja bez rekompresji (5) | Planowana |
| 0.6.2 | Zarządzanie kolorem i światło liniowe (4) | Planowana |
| 0.6.3 | AutoCrop, maski, mieszanie i korekcja tonalna (6) | Planowana |
| 0.7.0 | Integracja ICO i sześciu obszarów, stabilizacja API i wydanie | Planowana |

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

## 0.6.3 — operacje graficzne

Zakres:
- `AutoCrop`: obcięcie przezroczystych marginesów według progu alfy, opcjonalny padding; określone zachowanie całkowicie przezroczystego obrazu. Dla animacji wspólna obwiednia klatek zachowująca płótno i pozycje.
- Maski alfa: jawny wybór kanału alfa albo luminancji maski, pozycja i przycinanie, mnożenie istniejącej alfy bez zmiany RGB.
- Tryby mieszania `Normal`, `Multiply`, `Screen`, `Overlay`, `Darken`, `Lighten`, `Difference`, z opacity i poprawną kompozycją source-over. Współpraca z trybem liniowym z 0.6.2.
- Histogram RGB/luminancji z określoną polityką przezroczystych pikseli; wynik analizy nie modyfikuje obrazu.
- Gamma oraz poziomy wejściowe/wyjściowe, per RGB lub kanał; zdefiniowana kolejność i przestrzeń obliczeń, walidacja parametrów, alfa bez zmian.
- Operacje dostępne przez istniejący styl `Mutate`/`Clone`; analiza jako API odczytowe. Rysowanie tekstu, fonty i pełny silnik wektorowy poza zakresem.

DoD:
- Małe ręcznie policzone wektory dla każdego trybu, masek, histogramu, gammy i poziomów; testy alfy 0/255 i pośredniej, przycinania oraz granic parametrów.
- Testy animacji, `Clone`/`Mutate`, źródła będącego celem i braku niezamierzonych zmian RGB pod alfą.
- Pomiary pełnych operacji na małych grafikach i 12 MP; SIMD dopiero przy wykazanej korzyści i identyczności ze skalarem.

## 0.7.0 — integracja i stabilizacja

Zakres:
- Ukończyć DoD obsługi ICO i wszystkich sześciu obszarów. Ujednolicić opcje, nazwy, własność buforów, limity, anulowanie i polityki metadanych/koloru.
- Testy end-to-end: tworzenie i odczyt wielowariantowego ICO; miniatura JPEG z orientacją i konwersją ICC; sekwencyjna konwersja animacji z maską i resize; edycja metadanych bez rekompresji; przetwarzanie przy ograniczonych zasobach.
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
dotnet test -c Release --filter Category=Oracle
```
