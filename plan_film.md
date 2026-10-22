# PictureSharp — wywoływanie negatywów sfotografowanych telefonem

Status: **zatwierdzony** (decyzja użytkownika 2026-10-22): etapy A+B jako 0.7.11, C+D jako 0.7.12, oba przed
0.8.0; praca zaczyna się od etapu A. Zmiana (2026-10-22): 0.7.11 bez paczek NuGet, 0.7.12 to poprawka usuwania
kurzu (zjadało napisy: domyślnie wyłączone, test kształtu i izolacji plamki), etapy C+D przechodzą na 0.7.13. Nazwy API są robocze do czasu zatwierdzenia migawki.

## 1. Cel

Użytkownik fotografuje telefonem kliszę na podświetleniu i wywołuje ją jednym wywołaniem:

```csharp
using Image image = Image.Load("negatyw.png");
image.Mutate(x => x.DevelopNegative());   // kadr, prostowanie, maska, kolory, kurz, szum chrominancji
```

Filtr automatyczny sam znajduje klatkę, prostuje ją i kadruje, kalibruje maskę na brzegu kliszy, odwraca obraz w
gęstości optycznej, balansuje kolory i usuwa kurz. Te same kroki są dostępne osobno jako metody ręczne i jako analiza
z diagnostyką (dla interfejsów z suwakami i dla całej rolki z jedną kalibracją).

Zakres: kolorowy negatyw C-41 i czarno-biały negatyw, 35 mm z perforacją (pełna automatyka) oraz klatka bez
perforacji (np. 120 lub wcześniej wykadrowana — automatyka bez wykrywania geometrii). Poza zakresem: diapozytywy,
RAW/DNG, wejście HDR/16-bit, rozpoznawanie, gdzie jest góra sceny, retusz długich rys (inpainting) i usuwanie wzoru
monitora (FFT) — kandydaci na później (sekcja 8).

## 2. Ustalenia z próbek (prototyp numpy, 2026-10-22)

Próbki: prywatne zdjęcia iPhone'a w `../PictureSharp.Heif/samples/IMG_8157…8164.HEIC` (8 bit, 4:2:0, tryb makro
na ultraszerokim obiektywie). Prototyp i pomiary: `../PictureSharp.Heif/artifacts/negative-*.py`.

| Zdjęcie | Podświetlenie | Obcięty niebieski w klatce | Szum w. cz. | Wynik |
| --- | --- | ---: | ---: | --- |
| 8157 | białe, wykadrowane | 0,6% | — | dobre kolory po kalibracji z percentyli |
| 8158, 8161 | białe, rozproszone | 2,8% / 4,5% | 1,9 | niebo fioletowe/lawendowe |
| 8160, 8162 | niebieskie, rozproszone | 0,1% / 0,2% | 0,65 | najlepsze, naturalne niebo |
| 8163, 8164 | biały monitor, klisza na ekranie | 7,8% / 19,7% | 3,5 | mora z subpikseli |

Wnioski, które kształtują algorytm:
- **Naiwne `Invert` + `AutoContrast` daje płaski, zielonkawy obraz.** Działa dopiero odwrócenie w gęstości
  `D = −log10(T)` w świetle liniowym z osobnym zakresem gęstości dla każdego kanału.
- **Podłoże kliszy na brzegu jest najlepszym punktem czerni.** Lepszym niż percentyle. Otwory perforacji nie
  nadają się na absolutną biel: iPhone mapuje tony lokalnie (przy niebieskiej lampie podłoże wychodzi jaśniejsze
  w R niż lampa), więc kalibracja musi być lokalna.
- **Podświetlenie bywa nierówne o ≈10% między brzegami.** Trzeba je korygować płaszczyzną dopasowaną do podłoża.
- **Klisza bywa przekrzywiona (≈1,5°) albo obrócona o 90°, a klatka może być ucięta.** Wykrywanie geometrii jest
  częścią automatyki.
- **Próbki obcięte (0 lub 255) psują statystyki.** Trzeba je wykluczać i raportować.
- **Jednorodność podłoża (odchylenie 4–5 wobec 16–30 na monitorze) wykrywa teksturę podświetlenia.** Taki wynik
  to ostrzeżenie, nie powód do odmowy.
- **Kurz to drobne jasne punkty w pozytywie.** Plamy na niebie to szum chrominancji 8-bitowego HEVC wzmocniony
  odwróceniem.

Parametry prototypu, które stają się wartościami domyślnymi do strojenia: percentyl bieli 99,7, siła balansu tonów
średnich 0,6, kontrast papieru k = 1,6 dekady, krzywa S 0,3.

## 3. Publiczne API (`PictureSharp.Processing`, robocze)

Analiza (`ImageAnalysisExtensions`, obraz bez zmian, każda metoda także z `CancellationToken`):

```csharp
FilmFrame? image.FindFilmFrame(FilmFrameOptions? options = null);
NegativeAnalysis image.AnalyzeNegative(NegativeOptions? options = null);
```

Operacje (`ImageProcessingContext`):

```csharp
// automat: AnalyzeNegative → Rotate/Crop → InvertNegative → RemoveDust → ReduceColorNoise
DevelopNegative(NegativeOptions? options = null);
// samo odwrócenie tonów, bez geometrii i czyszczenia
InvertNegative(NegativeCalibration calibration, NegativePrintOptions? print = null);

// metody ręczne (etap A), przydatne także poza negatywami
Curves(ReadOnlySpan<PointF> points, ColorChannels channels = ColorChannels.Rgb);  // jak Levels/Gamma
WhiteBalance(Rgba32 neutral);               // wskazany punkt szarości (obok istniejącego temperature/tint)
AutoWhiteBalance(float strength = 1f);      // szary świat na tonach średnich, bez próbek obciętych
RemoveDust(int size = 3, float threshold = 0.1f);
ReduceColorNoise(float strength = 1f, int radius = 0);
```

Uściślenie etapu A (2026-10-22, przy implementacji): krzywa dla kilku kanałów to kilka wywołań `Curves` z
`ColorChannels`, tak jak `Levels`, więc osobny typ `ToneCurve` jest zbędny; `RemoveDust` przyjmuje największą
średnicę plamki (`size`), a promień `ReduceColorNoise` jest całkowity jak w `Denoise`.

Typy:
- `FilmType` (enum): `ColorNegative`, `BlackAndWhiteNegative`.
- `NegativeOptions` (rekord z walidowanymi `init`; stan po etapie B: `FilmType`, `Region`, `FilmBaseRegion`,
  `Calibration`, `HighlightPercentile`, `MidtoneBalance`, `DustSize`, `ColorNoiseReduction`, `Print` — szkic niżej):
  - `FilmType`;
  - nadpisania automatyki: `Frame` (`FilmFrame?`), `Calibration` (`NegativeCalibration?`) i `FilmBaseRegion`
    (`Rectangle?`);
  - przełączniki kroków: `Deskew`, `Crop`, `CorrectIllumination` (domyślnie true), `DustRemoval` (0–1, domyślnie
    0,5) i `ColorNoiseReduction` (0–1, domyślnie 0,5);
  - `Print` (`NegativePrintOptions`).
- `NegativePrintOptions`: `Contrast` (k), `Curve` (S), `MidtoneBalance`, `HighlightPercentile`, `Exposure` (EV).
- `FilmFrame`: `Bounds` (w układzie po wyprostowaniu), `Angle`, `PerforationsVertical`, `CutEdges` (flagi krawędzi
  ściętych przez kadr zdjęcia), `FilmBaseRegions` (prostokąty brzegu użyte do kalibracji).
- `NegativeCalibration`:
  - `FilmBase`, `DensityRange` i `MidtoneGamma` jako `ChannelValues(float Red, float Green, float Blue)`;
  - `static Combine(IEnumerable<NegativeCalibration>)` dla całej rolki: mediana podłoża, maksimum zakresu, mediana
    gammy.
- `NegativeAnalysis`: `Frame`, `Calibration`, `ClippedFraction` (`ChannelValues`), `FilmBaseUniformity` i
  `Warnings`.
- `NegativeWarnings` ([Flags]):
  - `ChannelClipped` (> 1% próbek obciętych);
  - `BacklightTexture` (niejednorodne podłoże: monitor bez dyfuzora);
  - `FrameCut`;
  - `FilmBaseEstimated` (brak brzegu, punkt czerni z percentyli);
  - `UnevenIllumination` (> 15% po korekcji);
  - `LowDensityRange`.

Wzorzec z diagnostyką bez podwójnej analizy oraz rolka z jedną kalibracją:

```csharp
NegativeAnalysis a = image.AnalyzeNegative();
if (a.Warnings.HasFlag(NegativeWarnings.ChannelClipped)) Console.WriteLine("Use a bluish backlight.");
image.Mutate(x => x.DevelopNegative(new NegativeOptions { Frame = a.Frame, Calibration = a.Calibration }));

NegativeCalibration roll = NegativeCalibration.Combine(frames.Select(f => f.AnalyzeNegative().Calibration));
foreach (Image f in frames) f.Mutate(x => x.DevelopNegative(new NegativeOptions { Calibration = roll }));
```

Zachowanie wspólne:
- Animacja (więcej niż jedna klatka) → `NotSupportedException`.
- Alfa zostaje bez zmian, a w pełni przezroczyste piksele nie wchodzą do statystyk.
- Wejście z ICC przechodzi najpierw przez `ConvertToSrgb`.
- Wynik jest deterministyczny i niezależny od liczby wątków.
- Istniejące operacje i ich wyniki bez zmian (wszystko addytywnie).

## 4. Algorytmy (`Algorithms/Processing/Film/`, internal)

1. **Podgląd analizy.** Analiza działa na pomniejszonej kopii (dłuższy bok ≈1024 px, `Resizer` w świetle liniowym).
   Wyniki geometrii są skalowane do pełnej rozdzielczości, a wszystkie statystyki liczone na podglądzie. Dzięki
   temu koszt analizy nie zależy od rozmiaru zdjęcia.
2. **`FindFilmFrame` (`FilmFrameDetector`).**
   - Klasy pikseli: tło lub lampa (najjaśniejszy mod jasności), klisza (reszta) i podłoże (najjaśniejszy mod gęstości
     wewnątrz kliszy).
   - Perforacja: jasne, prostokątne plamy tła w całości otoczone kliszą, o podobnym polu i z regularnym skokiem.
     Dwa rzędy dają kierunek kliszy (`PerforationsVertical`) i kąt (prosta MNK przez środki otworów, z odrzuceniem
     odstających), a skok otworów (4,75 mm) daje skalę px/mm.
   - Klatka: między rzędami perforacji profil udziału pikseli gęstszych od podłoża wyznacza krawędzie klatki
     i przerwy międzyklatkowe. Kontrola z geometrią 36×24 mm w skali z perforacji; klatka najbliższa środka zdjęcia
     wygrywa. Krawędź na brzegu zdjęcia oznacza `CutEdges`.
   - Bez perforacji: największy prostokąt gęstszy od otaczającego podłoża. Bez podłoża zwraca `null` (cały obraz
     jest klatką).
   - Regiony podłoża: pasy między perforacją a klatką i przerwy międzyklatkowe, bez otworów. Napisy brzegowe (np.
     KODAK) i kod kreskowy są gęste, więc odpadają w odrzucaniu odstających (mediana ± 3·MAD).
3. **Kalibracja (`NegativeCalibrator`).**
   - Liniowo: `SrgbTransfer`, a logarytmy przez `DeterministicMath.Log2`.
   - Podłoże: mediana z regionów brzegu w każdym kanale. Z `CorrectIllumination` dopasowujemy płaszczyznę
     `log B(x, y) = a + b·x + c·y` (MNK po próbkach brzegu), a `UnevenIllumination` zgłaszamy wtedy, gdy dopasowanie
     zostawia > 15%.
   - Gęstość: `d = log10(B(x, y) / max(v, ε))` z ε = 1/1024. Próbki z kanałem ≤ 2 lub ≥ 253 wypadają ze statystyk
     i są liczone w `ClippedFraction`.
   - Zakres: `DensityRange` = percentyl `HighlightPercentile` gęstości w każdym kanale.
   - Tony średnie: `t = d / range`, potem gamma na kanał tak, by mediany zbliżyły się do ich średniej
     geometrycznej, z siłą `MidtoneBalance`.
   - Bez brzegu (`FilmBaseEstimated`): podłoże z percentyla 0,3 najjaśniejszych próbek, jak w prototypie dla 8157.
   - `FilmBaseUniformity`: odchylenie w regionach brzegu po korekcji płaszczyzną. Próg `BacklightTexture` ustalimy
     z próbek (8160: 4–5, 8163/8164: 16–30).
4. **Odwrócenie (`NegativePrinter`), jedno przejście, float wewnątrz, 8 bit tylko na końcu.**
   - Kroki: `t^γ` → `pos = 10^(k·(t − 1))` → normalizacja bieli (percentyl 99,8 średniej kanałów z podglądu,
     ekspozycja w EV) → sRGB → krzywa S `s + c·s(1 − s)(2s − 1)`.
   - Bez korekcji podświetlenia cały łańcuch to LUT 256 na kanał (`ToneLut`).
   - Z korekcją: LUT 256 dla `log v`, przesunięcie `log B(x, y)` liczone na wiersz, końcowy LUT 4096 po `t`
     z interpolacją liniową w stałym porządku operacji.
   - Rozkład pracy przez `Parallelism.ForBands`.
   - Czarno-biały negatyw: jedna gęstość z lumy, ten sam wydruk, wynik szary.
5. **Geometria.** Istniejące `Rotate(angle, Bicubic)` i `Crop` (kadr wewnątrz klatki z marginesem 0,5% na
   zaokrąglone narożniki). Obrót o 90° wynika z `PerforationsVertical`. Kierunek sceny wybiera użytkownik
   (`Rotate(RotateMode)`).
6. **`RemoveDust(size, threshold)`.**
   - Wykrywanie na lumie Y = (2126 R + 7152 G + 722 B + 5000) / 10000 (całkowicie).
   - Piksel jest plamką, gdy różni się o więcej niż `threshold`·255 w tę samą stronę od median wszystkich czterech
     odcinków długości 2·size + 1 przez niego (poziomo, pionowo i po przekątnych). Plamka o średnicy ≤ size zajmuje
     mniejszość każdego odcinka, a krawędź lub rysa zawsze ma odcinek równoległy do siebie, więc zostaje (rysy to
     etap inpaintingu).
   - Maska jest powiększona o jeden piksel tam, gdzie sąsiad odstaje w tę samą stronę o więcej niż połowę progu od
     mediany okna (obwódka plamki).
   - Piksele maski dostają medianę okna (2·size + 1)² w każdym kanale; alfa bez zmian.
   - Mediana n wartości to element (n − 1) / 2 po posortowaniu. Okna i odcinki są przycięte do obrazu.
7. **`ReduceColorNoise(strength, radius)`.**
   - Na wartościach premultiplied (jak `Denoise`): Y z wagami Rec. 709, Cb = B − Y, Cr = R − Y.
   - Cb i Cr wygładzone guided filtrem prowadzonym przez Y (He i in.) z ε = 0,01² i oknem (2r + 1)². Promień 0 to
     1% krótszego boku, co najmniej 2.
   - Wynik mieszany z wejściem w proporcji `strength`; Y zostaje bez zmian, G odtworzone z Y.
   - Usuwa plamy z niebieskiego kanału HEVC, a krawędzie barwne pokrywające się z krawędziami lumy zostają.
8. **`Curves(points, channels)`.**
   - Punkty (wejście, wyjście) w zakresie 0–255 o rosnących X, co najmniej dwa.
   - Interpolacja PCHIP (Fritsch–Butland, jak `scipy.interpolate.PchipInterpolator`): kształt zachowany, bez
     przerzutów. Poza skrajnymi punktami wartość stała.
   - Wynik to LUT 256 w double, zaokrąglany w górę od połówki.

   **`WhiteBalance(neutral)` i `AutoWhiteBalance`** to wzmocnienia w świetle liniowym jako LUT, tak jak istniejące
   `WhiteBalance`.

## 5. Etapy

| Etap | Zakres | Wartość sama w sobie |
| --- | --- | --- |
| A | `Curves`/`ToneCurve`, `WhiteBalance(Rgba32)`, `AutoWhiteBalance`, `RemoveDust`, `ReduceColorNoise` | ręczna obróbka skanów i zdjęć |
| B | `AnalyzeNegative` bez geometrii (cały obraz lub `FilmBaseRegion`), `NegativeCalibration`, `InvertNegative`, `DevelopNegative` bez kadrowania | odwracanie wykadrowanych negatywów (jak 8157) |
| C | `FindFilmFrame`, korekcja podświetlenia, ostrzeżenia, prostowanie i kadr w `DevelopNegative` | pełna automatyka (8158, 8160–8164) |
| D | `NegativeCalibration.Combine`, strojenie wartości domyślnych na próbkach, dokumentacja, benchmarki, wydanie | rolka z jednym wyglądem |

Propozycja wydań: A+B jako 0.7.11 i C+D jako 0.7.12, przed 0.8.0 (po poprawce 0.7.12: C+D jako 0.7.13). Alternatywa: całość po 0.8.0. Decyzja należy do
użytkownika.

### Stan etapu A (2026-10-22)

Zaimplementowane: `Curves`, `WhiteBalance(Rgba32)`, `AutoWhiteBalance`, `RemoveDust`, `ReduceColorNoise`
(`ToneCurve`, `GrayWorld`, `DustFilter`, `ChromaFilter`, `ImageProcessingContext.Cleanup.cs`).
- Wyrocznia numpy (`photo_adjust_reference.py`, nowy obraz `dust.png` z plamkami, linią i krawędzią): 147/148
  przypadków identycznych, 1 w ±1.
- `PhotoCleanupTests` 11/11, SHA-256 pięciu nowych operacji identyczne przy 8 i 1 wątku.
- Migawka API: +5 metod.
- 12 Mpx (photo-probe): Curves 11 ms, WhiteBalance(neutral) 10 ms, AutoWhiteBalance 57 ms (liczenie jednowątkowe),
  RemoveDust(6) 177 ms, ReduceColorNoise 205 ms; pamięć tylko klon obrazu.

Zmiany względem projektu po próbach na 8160:
- Próg `RemoveDust` rośnie do 3σ szumu kafla 32×32 (MAD), bo przy stałym progu ziarno było brane za kurz.
- Obwódka plamki rośnie do `size` rund zamiast jednego piksela.
- Mediana odcinka jest liczona zliczaniem zamiast sortowania (wynik ten sam).

Ograniczenie: miękka plamka o amplitudzie na poziomie ziarna (8160: 29 poziomów przy σ ≈ 10) zostaje; takie
plamki odróżnia tylko kanał podczerwieni skanera.

### Stan etapu B (2026-10-22)

Zaimplementowane: `AnalyzeNegative` (bez geometrii), `NegativeCalibration` (+ `Combine`), `InvertNegative`,
`DevelopNegative` bez kadrowania; typy `NegativeOptions`, `NegativePrintOptions`, `NegativeAnalysis`, `FilmType`,
`ChannelValues`, `NegativeWarnings` (`ChannelClipped`, `FilmBaseEstimated`, `LowDensityRange`; reszta w etapie C).
Kod: `Algorithms/Processing/Film/` (`NegativeCalibrator`, `NegativePrinter` = LUT 3×256 w double),
`ImageProcessingContext.Film.cs`.

Decyzje przy implementacji:
- Bez konwersji do sRGB przed odwróceniem. Display P3 → sRGB obcinało maskę (8160: 6.8% zamiast 0.1% obciętego
  niebieskiego); odwracamy zapisane wartości (krzywa sRGB), profil zostaje przy pozytywie.
- Parametry pomiaru (`HighlightPercentile`, `MidtoneBalance`) w `NegativeOptions`, druku (`Contrast`, `Curve`,
  `Exposure`) w `NegativePrintOptions`; bez normalizacji bieli z prototypu (t = 1 drukuje się na biało).
- Próbkowanie co k-ty wiersz i kolumnę (~1 Mpx), percentyle jako najbliższa ranga z histogramów całkowitych.
- `MidtoneBalance` 0.6 zostaje domyślne: na 8157, 8160, 8162 i Ektarze 0 daje zielono-cyjanową dominantę.
- Próg jakości etapu B zmieniony: PSNR ≥ 18 dB przy domyślnym druku nie ma sensu (papier 1.6 dekady vs film
  syntetyczny 2.4 dekady, wynik 15.7 dB to różnica tonów). Obowiązuje: przy druku dopasowanym do filmu
  syntetycznego (Contrast 2.4, Curve 0, MidtoneBalance 0) PSNR ≥ 30 dB i klin szarości neutralny ±8 poziomów.

Wyniki: wyrocznia numpy 9/9 analiz (1e-5) i 12/12 obrazów identycznych; `FilmNegativeTests` 8/8; SHA-256
`DevelopNegative` identyczne przy 8 i 1 wątku; migawka API: tylko dodatki. Na zdjęciach: 8160 kalibracja zgodna
z prototypem co do 3 cyfr; czasy analiza 11–22 ms, develop 92–278 ms (2–5 Mpx).

## 6. Testy i wyrocznie

- **Referencja numpy** `tests/assets/film_negative_reference.py`, analogiczna do `photo_adjust_reference.py`:
  kalibracja i wydruk tymi samymi wzorami. Tolerancja ±1 na pikselach i 1e-4 na parametrach kalibracji.
- **Korpus syntetyczny** w `tests/assets/corpus/film/` (≤ 1 Mpx na plik) z generatora
  `tests/assets/film_corpus.py`. Generator robi z pozytywu negatyw:
  - odwrotność modelu wydruku z różną gammą warstw;
  - pomarańczowa maska;
  - gradient podświetlenia;
  - pasek z perforacją, napisami i przerwami międzyklatkowymi;
  - obrót o ±0–3° lub 90°;
  - ucięta klatka;
  - szum, kurz;
  - wariant z siatką „monitora”.

  Asercje:
  - ramka ±0,5% boku, kąt ±0,1°, poprawne `PerforationsVertical` i `CutEdges`;
  - podłoże ±1%;
  - pozytyw po `DevelopNegative` wobec oryginału: średnie ΔE2000 < 3 i PSNR > 30 dB (progi ustalimy przed
    implementacją etapu B);
  - ostrzeżenia zgodne z wariantem.
- **Właściwości:**
  - tożsamość `Curves` dla przekątnej;
  - `WhiteBalance(neutral)` daje szary piksel neutralny;
  - `RemoveDust` nie zmienia obrazu bez kurzu poza progiem;
  - `ReduceColorNoise` nie zmienia Y;
  - walidacja opcji, alfa, przezroczystość w statystykach, `NotSupportedException` dla animacji, anulowanie;
  - SHA-256 w `ProcessingDeterminismTests` przy 1 i 8 wątkach.
- **`Category=Local`:**
  - zdjęcia 8157–8164 przekonwertowane lokalnie do PNG (prywatne, nieśledzone);
  - ramki zgodne z ręcznymi z prototypu;
  - `BacklightTexture` dla 8163/8164 i `ChannelClipped` dla 8158/8161/8164, a nie dla 8160/8162;
  - podgląd do oceny wzrokowej użytkownika.
- **Benchmark** `FilmNegativeBenchmarks` na 12 Mpx. Cel: `DevelopNegative` < 0,5 s na 8 wątkach, analiza < 50 ms.
  SIMD tylko według pomiarów.

## 7. DoD

- Etapy A–D zaimplementowane, testy z sekcji 6 przechodzą, a progi jakości są ustalone i zapisane przed
  implementacją.
- `docs/image-processing.md`: sekcja o negatywach ze wzorami, przykładami (`DocsExamplesTests`) i wskazówkami
  fotografowania:
  - rozproszone, lekko niebieskie podświetlenie;
  - klisza nad ekranem albo dyfuzor;
  - brzeg kliszy w kadrze;
  - bez obcięcia.
- Migawka API z samymi dodatkami, notatki wydania i wspólna bramka jakości z `plan.md`.

## 8. Poza zakresem — kandydaci

- Retusz z maską (inpainting) dla rys i pasów.
- Usuwanie wzoru okresowego monitora (notch w FFT).
- Diapozytywy.
- Rozpoznawanie orientacji sceny.
- Wejście 16-bit/HDR po wprowadzeniu przetwarzania float w rdzeniu.
- Automatyczne łączenie dwóch ekspozycji (bracketing) dla gęstego nieba.
