# Weryfikacja domeny i C# 14 w PictureSharp

Release note: these measurements were captured before publication; the verified changes are included in 0.5.5.

Data: 2026-10-15. Zmiany lokalne po opublikowanej wersji 0.5.4; VERSION pozostaje 0.5.4.

## Wynik

Zrealizowano plan wzmocnienia modelu oraz domknięto weryfikację wcześniejszych usprawnień. Image zatwierdza wspólne płótno i wszystkie bufory po walidacji; aplikacja przygotowuje wyniki, obsługuje anulowanie i pulę buforów. Wprowadzono wewnętrzne ImageDimensions, kontrolowane tokeny własności bez dodatkowej alokacji, walidację konstruktorów wewnętrznych i nieujemnych czasów klatek, odporną na przepełnienia walidację prostokątów oraz niemodyfikowalne rozszerzenia formatów.

Transfer klatki przygotowuje wynik i metadane przed usunięciem źródła. Dostęp do stanu obrazu i jego kolekcji po Dispose jest spójny; wcześniej uzyskane widoki pozostają niemożliwe do unieważnienia. ICO zachowuje niezależne warianty. Publiczne metadane zwracają kopie; GetBlockLength i CopyBlockTo pozwalają uniknąć kopii przy odczycie rozmiaru i użyciu własnego bufora. Edycja orientacji zachowuje nieznane dane i odrzuca ucięty katalog przed zmianą bloku.

Zachowano publiczne Image, ImageFrame, Size, Rectangle i TimeSpan. Zastosowano field do walidowanych właściwości i extension do fasady processing. Reguły domeny są metodami modelu. Konwersje spanów oraz priorytety przeciążeń sprawdza test kompilacji konsumenta C# 14.

## Testy i narzędzia

| Kontrola | Wynik |
| --- | --- |
| Build całej solucji Release | 0 ostrzeżeń, 0 błędów |
| Pełne testy | 5134/5134, 0 pominiętych |
| Oracle | 150/150; libwebp 1.6.0, Pillow 10.2.0, NumPy 1.26.4 dostępne |
| SIMD przy DOTNET_EnableHWIntrinsic=0 | 113/113 |
| Migawka API | Zgodna; sprawdzone nowe grupy opcji, operacje klatek/metadanych oraz atrybuty required/extension/priorytetów |
| Lokalny NuGet i symbole | .nupkg i .snupkg zbudowane |
| Zawartość NuGet | Biblioteka, XML, README, LICENSE i notices; brak zależności i runtime/native assetów |
| Konsument paczki z pełnym trimmingiem | Publikacja i wykonanie linux-x64 bez ostrzeżeń |
| Konsument paczki Native AOT | Kompilacja i wykonanie linux-x64 bez ostrzeżeń |
| CI | YAML i zależność publikacji od workflow walidacji sprawdzone lokalnie |

Punkt odniesienia obecnego etapu miał 5087 testów: 5086 zaliczonych i nieaktualną migawkę API po poprzednich zmianach. Migawkę przejrzano i uaktualniono. Obecny etap dodał 47 przypadków, w tym kontrakty siedmiu kodeków, sesji, budżetów, izolowanych usług, własności, anulowania i edycji EXIF. Testy architektury badają sygnatury i IL, ograniczają publikowanie buforów oraz attach/detach i zawierają testy własnego inspektora.

Konsument używa paczki z lokalnego feedu, izolowanego cache i source mapping wskazującego PictureSharp wyłącznie na ten feed. Sprawdza wszystkie formaty, przetwarzanie, async, piksele, ICO, animację, miniatury JPEG, grupę opcji PNG oraz nowe operacje metadanych i transferu klatki. Odtwarzanie: `bash scripts/verify-package-consumer.sh`.

## Wydajność

Ta sama maszyna x64 Intel Core i5-12500H, SDK 10.0.112, runtime 10.0.12, affinity CPU 0–7. Istniejąca MatrixBenchmarks: 4 wejścia × 20 operacji, 80 przypadków. Każdy pomiar to mediana 7 próbek po co najmniej 120 ms. Wykonano kolejność przed → po → po → przed; mediana zmian całej macierzy wyniosła +0.66%.

Dziesięć agregatów przekroczyło początkowo 5%. Sprawdzono je dla końcowych binariów naprzemiennie w jednym procesie, w osobnych AssemblyLoadContexts, po rozgrzewce, w 15 próbkach każdej wersji:

| Wejście | Operacja | Zmiana czasu |
| --- | --- | --- |
| `graphic.png` | WebPLosslessDecode | +1.76% |
| `graphic.png` | WebPLosslessDecode1T | +0.72% |
| `graphic.png` | PngDecode | -0.26% |
| `graphic.png` | JpegDecode | +0.14% |
| `graphic.png` | BmpDecode | +0.27% |
| `graphic.png` | GifDecode | -0.18% |
| `graphic.png` | PcxDecode | -2.61% |
| `graphic.png#8x` | WebPLossyEncode | +4.50% |
| `graphic.png#8x` | WebPLossyDecode | -1.25% |
| `graphic.png#8x` | GifDecode | +0.94% |

Najbliższy progowi WebPLossyEncode dla dużej grafiki powtórzono: +1.97%. Dodatkowo WebPLossyEncode1T: −0.63%, różnica alokacji 0 B; początkowy wzrost +1710 B/op nie powtórzył się. Pozostałe dodatnie różnice alokacji w pełnej macierzy wyniosły poniżej 0.03%. Nie dodano alokacji w gorących pętlach algorytmów. Nie potwierdzono powtarzalnej regresji czasu powyżej 5%.

Pełna tabela: [ddd-benchmarks.csv](ddd-benchmarks.csv). Są to pomiary lightweight probe istniejącej macierzy oraz dokładniejsze porównania naprzemienne, a nie pełne badanie BenchmarkDotNet. Późniejsze doprecyzowanie walidacji uszkodzonego EXIF i anulowania sesji nie zmienia ścieżek tej macierzy; porównania podejrzanych przypadków używają końcowych binariów.

## CI i dokumentacja

Workflow walidacji dla push/PR/workflow_call obejmuje managed tests, fallbacki, przypięte wyrocznie i konsumenta paczki trim/AOT. Workflow wydania zależy od tej walidacji. Dodano przewodnik rozszerzania biblioteki oraz uzupełniono architekturę, migrację API, opcje, metadane, zasady anulowania, AGENTS.md i plan rozwoju.

## Granice weryfikacji

- ARM64 nie sprawdzono na rzeczywistym sprzęcie.
- Zdalnego przebiegu GitHub Actions nie uruchomiono; workflow jest przygotowany i lokalne odpowiedniki kontroli zakończyły się powodzeniem.
- Sesje sekwencyjne pozostają wewnętrznymi kontraktami z testowanym cyklem życia. Produkcyjne kodeki sekwencyjne i publiczny streaming są osobnym etapem 0.6.0.
- Nie wykonano commita, pusha, zmiany wersji ani publikacji.
