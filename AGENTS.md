# AGENTS.md

Правила и ориентиры для ИИ-агентов, работающих с репозиторием **BirdCam**.
Читать перед любыми изменениями кода, настроек или документации.

## 1. Что это за проект

BirdCam — Windows-приложение (WPF, .NET 10) для наблюдения за птицами у кормушки
через камеру Xiaomi C200:

- видео приходит по RTSP от локального [go2rtc](https://github.com/AlexxIT/go2rtc);
- кадры обрабатываются YOLO-моделью в ONNX Runtime (DirectML/CPU);
- найденные птицы складываются в датасет (изображение + YOLO-разметка + кроп);
- события и файлы хранятся в SQLite;
- фото/видео могут отправляться в Telegram;
- на вкладке «Обучение» датасет дообучается через ultralytics, а `best.onnx`
  устанавливается как активная модель;
- go2rtc запускается и останавливается вместе с приложением.

Пользовательский интерфейс и сообщения — на русском, код и имена — на английском.

## 2. Быстрый старт

```powershell
dotnet build BirdCam.slnx
dotnet test BirdCam.Tests/BirdCam.Tests.csproj
dotnet run --project BirdCam.UI
```

Требования:

- Windows 10/11, .NET 10 SDK;
- ffmpeg (winget-версия находится автоматически, путь можно задать в настройках);
- go2rtc (`go2rtc.exe`; приложение умеет запускать его само);
- ONNX-модель в `models/` — веса в репозиторий не коммитятся;
- для обучения — Python 3.12/3.13 с `ultralytics`, `onnx`, `onnxslim`, `onnxruntime`
  (`.tools\venv` или `training\venv`; путь можно задать в настройках).

Данные и логи: `%LOCALAPPDATA%\BirdCam\`
(`settings.json`, `birdcam.db`, `media\`, `dataset\`, `training\`, `models\`, `logs\`).

## 3. Структура решения

| Проект | Слой | Содержимое |
|---|---|---|
| `BirdCam.Domain` | Domain | сущности, enum'ы, `VideoFrame`, `BoundingBox`; без внешних зависимостей |
| `BirdCam.Application` | Application | интерфейсы, сценарии, настройки, letterbox/NMS, YOLO-разметка; без OpenCV/EF/Telegram |
| `BirdCam.Infrastructure` | Infrastructure | OpenCV, ONNX Runtime, EF Core + SQLite, ffmpeg, Telegram.Bot, go2rtc, запуск ultralytics, JSON-настройки, логи |
| `BirdCam.UI` | UI | WPF + MVVM (CommunityToolkit.Mvvm), DI через Generic Host, страницы и ViewModels, вкладка обучения |
| `BirdCam.Tests` | Tests | xUnit-тесты чистой логики (без камеры и сети), сплита датасета и разбора ONNX-метаданных |

Ключевые файлы:

- `BirdCam.UI/App.xaml.cs` — DI, старт/остановка go2rtc, запуск окна, CLI-режимы;
- `BirdCam.Infrastructure/Capture/OpenCvFrameSource.cs` — RTSP-захват;
- `BirdCam.Infrastructure/Detection/OnnxBirdDetector.cs` — ONNX + DirectML;
- `BirdCam.Infrastructure/Go2Rtc/Go2RtcManager.cs` — жизненный цикл go2rtc;
- `BirdCam.Application/Services/BirdWatcher.cs` — фоновая детекция и датасет;
- `BirdCam.Application/Services/TrainingService.cs` — сценарий обучения и установка модели;
- `BirdCam.Infrastructure/Training/TrainingDataService.cs` — аудит датасета и train/val;
- `BirdCam.Infrastructure/Training/UltralyticsTrainingRunner.cs` — Python-процесс обучения;
- `BirdCam.Infrastructure/Training/TrainingScript.cs` — встроенный `train.py`;
- `BirdCam.UI/ViewModels/TrainingViewModel.cs` — вкладка «Обучение»;
- `BirdCam.UI/ViewModels/SettingsViewModel.cs` — сохранение настроек;
- `BirdCam.UI/Views/*.xaml` — страницы;
- `docs/training.md` — гайд по обучению (локально, вручную, Colab);
- `docs/training-colab.ipynb` — готовый блокнот Colab.

## 4. Архитектурные правила

1. **Направление зависимостей только внутрь:**
   `UI → Infrastructure → Application → Domain`.
   Domain не ссылается ни на что; Application не знает про OpenCV, EF Core, Telegram.
2. Новые интерфейсы — в `BirdCam.Application/Abstractions`, реализации — в Infrastructure.
3. Оркестрация (сценарии) — в Application-сервисах; UI вызывает сервисы и команды,
   а не работает с БД/камерой напрямую.
4. Все настройки — через `ISettingsProvider` и `AppSettings`; значения по умолчанию
   задаются в `AppSettings`. Не читать конфиги напрямую из UI.
5. Использовать `ILogger<T>`, `TimeProvider`, `CancellationToken`; не блокировать UI-поток.
6. `VideoFrame` арендует буфер из пула — **всегда** освобождать (`using`/`Dispose`),
   не сохранять `Pixels` после освобождения.
7. В БД хранятся пути **относительно** корня хранилища; для файлов использовать
   `IFileStorage`, а не `File.*` напрямую из UI.
8. go2rtc: останавливать только тот процесс, который запустило приложение.
   Внешний go2rtc не убивать. Перед запуском проверять API `http://127.0.0.1:1984/api`.
9. Детектор поддерживает стандартный экспорт YOLOv8/v11/v26 с выходом
   `[1, 4 + classes, N]`. Экспорт `nms=False` (`[1, 300, 6]`) не поддерживается.
   Класс птицы задаётся настройкой `BirdClassIds` (COCO — `14`, своя модель — обычно `0`).
10. Датасет: класс `0 = bird`, координаты нормализованы; кропы — для классификатора видов.
11. Обучение: `TrainingDataService` делает сплит по времени (последние
    `ValidationPercent` кадров — val), пропускает файлы без рамок и пишет
    `data.yaml` **без `path`** + относительные `./images/...` в `train.txt`/`val.txt`.
    Так датасет переносим и работает в Colab. Не возвращать абсолютные пути.
12. Python-процесс обучения запускается только через `ITrainingRunner`, с рабочей
    папкой `training`, `TQDM_DISABLE=1`, UTF-8 stdout и прогрессом по строкам
    `BIRDCAM:{json}`. Отменять/закрывать процесс нужно kill'ом дерева; при выходе
    приложения runner убивает только свой процесс.
13. Python ищется в настройках, `training/venv`, `.tools/venv` (вверх по дереву
    от exe), PATH и `py -3.13`. Не хардкодить путь к `.tools`.

## 5. Соглашения кода

- `Nullable` и `ImplicitUsings` включены; файловые namespace; primary constructors — где уместно.
- Асинхронные методы — суффикс `Async`; в Infrastructure — `ConfigureAwait(false)`.
  В UI-потоке — `Dispatcher`/`BeginInvoke`, без `.Result`/`.Wait()` (кроме завершения приложения).
- MVVM: `[ObservableProperty]`, `[RelayCommand]`; логика — в ViewModel, не в code-behind.
- Ошибки ловить на границе (UI/сервис), логировать и показывать пользователю через
  `StatusService`; не глотать исключения молча.
- Строки интерфейса — русские, сообщения логов — русские; идентификаторы — английские.
- Комментарии — только там, где неочевидно; XML-doc на публичных интерфейсах.
- Секреты (токен Telegram, passToken Xiaomi) — только в `settings.json`/`go2rtc.yaml`,
  никогда в коде, логах и git.
- Стремиться к сборке без предупреждений; не отключать анализаторы без причины.

## 6. Диагностические режимы

```powershell
BirdCam.UI.exe --smoke                              # кадр → детекция → снимок → датасет → клип
BirdCam.UI.exe --smoke --image C:\temp\bird.jpg     # детекция на картинке
BirdCam.UI.exe --ui-smoke                           # загрузка всех страниц XAML + overlay
BirdCam.UI.exe --page gallery                       # открыть страницу
BirdCam.UI.exe --screenshot settings --out C:\temp\settings.png
BirdCam.UI.exe --training-check                     # датасет + Python/ultralytics
BirdCam.UI.exe --training-check --training-smoke     # одна эпоха 320px, проверка тракта
BirdCam.UI.exe --training-check --training-ui --epochs 1 --imgsz 320 --batch 4 --workers 0
BirdCam.UI.exe --training-check --install C:\path\best.onnx
```

Результаты: `%LOCALAPPDATA%\BirdCam\logs\smoke.log`, `ui-smoke.log`, `training-check.log`,
`training-ui.log`, `birdcam-YYYYMMDD.log`.

`run-quiet.cmd` задаёт `OPENCV_FFMPEG_LOGLEVEL=8` и `OPENCV_FFMPEG_CAPTURE_OPTIONS=rtsp_transport;tcp`
**до** запуска процесса: OpenCV читает их при загрузке нативной библиотеки, поэтому из кода
приложения это уже поздно.

## 7. Проверка перед завершением задачи

1. `dotnet build BirdCam.slnx` — 0 ошибок (и 0 предупреждений).
2. `dotnet test BirdCam.Tests/BirdCam.Tests.csproj` — все тесты зелёные.
3. Если затронут XAML/UI — `--ui-smoke`.
4. Если затронуты захват/детекция/клипы/датасет — `--smoke` (нужны go2rtc и камера).
5. Если затронуто обучение — `--training-check`; для изменений раннера —
   `--training-check --training-ui --epochs 1 --imgsz 320 --batch 4 --workers 0`
   (полные 40 эпох в проверку не запускать).
6. Если менялось поведение — обновить `README.md`; для обучения — `docs/training.md`.
7. Не коммитить `models/`, `.tools/`, `bin/`, `obj/`, `settings.json`, `*.db`.

## 8. Частые грабли

- **Стоп → Запуск, чёрный экран.** При остановке обнулять `_bitmap`, а на тике
  перепривязывать `Frame` к новому `WriteableBitmap` (см. `LiveViewModel`).
- **ComboBox и null.** `SelectedItem` может записать `null` в строковое свойство.
  Не вызывать `.Trim()` без проверки; коллекции, привязанные к ComboBox, не очищать
  через `Clear()` — обновлять дифференциально.
- **HEVC-предупреждения** `Could not find ref with POC ...` — штатные при старте с середины GOP.
  Подавляются только переменными окружения до запуска (см. раздел 6).
- **go2rtc не запущен** — приложение должно поднять его само; проверка API и понятное
  сообщение обязательны. Внешний экземпляр не останавливать.
- **`EnsureCreated`** — схема БД создаётся один раз. При изменении модели EF нужно
  либо делать миграцию, либо явно предупредить о пересоздании `birdcam.db`.
- **Модели не в git.** После клонирования `models/` пустая — экспорт:
  `yolo export model=yolo26m.pt format=onnx opset=18 imgsz=640`.
- **Telegram** принимает видео до 50 МБ; клипы пишутся в H.264/AAC.
- **FFmpeg-путь** ищется в настройках, PATH и winget — не хардкодить.
- **Обучение на CPU.** PyTorch на Windows/AMD не использует GPU: ~40 с/эпоха
  при 640px и 328 кадрах. Не обещать ускорение через DirectML.
- **`trainer.loss_items`** в ultralytics 8.4 — словарь, а не тензор. В `train.py`
  усреднять значения через `mean_loss`, иначе callback падает с `'dict' object
  has no attribute 'mean'`.
- **ONNX-метаданные `names`.** Ultralytics пишет Python-dict с кириллицей;
  разбирать `YoloLabels.ParseNames`, не `JSON.parse`. В консоли Windows кириллица
  может выглядеть как `????`, проверять байты/через детектор.
- **Датасет и Colab.** `data.yaml` без `path` + `./images/...` в списках — обязательное
  условие переносимости. Абсолютные пути ломают Colab.
- **Один класс.** Автосбор пишет только `0 = bird`; «синица» — это имя класса,
  а не многоклассовая модель. Не утверждать, что модель различает другие виды.
- **Пустые labels.** Пропускаются при сплите; в датасете могут быть битые HEVC-кадры
  (серый шум) — это нормально, обучение их не берёт.

## 9. Git

- Коммиты — атомарные, сообщения на английском в повелительном наклонении
  (`Fix ...`, `Add ...`, `Shorten ...`).
- Push — только по явной просьбе пользователя.
- Перед коммитом проверить `git status` и не добавлять сгенерированные/локальные файлы.
- Публичный репозиторий: не коммитить скриншоты с приватным видом камеры без согласия.
