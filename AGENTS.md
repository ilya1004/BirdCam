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
- ONNX-модель в `models/` — веса в репозиторий не коммитятся.

Данные и логи: `%LOCALAPPDATA%\BirdCam\`
(`settings.json`, `birdcam.db`, `media\`, `dataset\`, `models\`, `logs\`).

## 3. Структура решения

| Проект | Слой | Содержимое |
|---|---|---|
| `BirdCam.Domain` | Domain | сущности, enum'ы, `VideoFrame`, `BoundingBox`; без внешних зависимостей |
| `BirdCam.Application` | Application | интерфейсы, сценарии, настройки, letterbox/NMS, YOLO-разметка; без OpenCV/EF/Telegram |
| `BirdCam.Infrastructure` | Infrastructure | OpenCV, ONNX Runtime, EF Core + SQLite, ffmpeg, Telegram.Bot, go2rtc, JSON-настройки, логи |
| `BirdCam.UI` | UI | WPF + MVVM (CommunityToolkit.Mvvm), DI через Generic Host, страницы и ViewModels |
| `BirdCam.Tests` | Tests | xUnit-тесты чистой логики (без камеры и сети) |

Ключевые файлы:

- `BirdCam.UI/App.xaml.cs` — DI, старт/остановка go2rtc, запуск окна, CLI-режимы;
- `BirdCam.Infrastructure/Capture/OpenCvFrameSource.cs` — RTSP-захват;
- `BirdCam.Infrastructure/Detection/OnnxBirdDetector.cs` — ONNX + DirectML;
- `BirdCam.Infrastructure/Go2Rtc/Go2RtcManager.cs` — жизненный цикл go2rtc;
- `BirdCam.Application/Services/BirdWatcher.cs` — фоновая детекция и датасет;
- `BirdCam.UI/ViewModels/SettingsViewModel.cs` — сохранение настроек;
- `BirdCam.UI/Views/*.xaml` — страницы.

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
```

Результаты: `%LOCALAPPDATA%\BirdCam\logs\smoke.log`, `ui-smoke.log`, `birdcam-YYYYMMDD.log`.

`run-quiet.cmd` задаёт `OPENCV_FFMPEG_LOGLEVEL=8` и `OPENCV_FFMPEG_CAPTURE_OPTIONS=rtsp_transport;tcp`
**до** запуска процесса: OpenCV читает их при загрузке нативной библиотеки, поэтому из кода
приложения это уже поздно.

## 7. Проверка перед завершением задачи

1. `dotnet build BirdCam.slnx` — 0 ошибок (и 0 предупреждений).
2. `dotnet test BirdCam.Tests/BirdCam.Tests.csproj` — все тесты зелёные.
3. Если затронут XAML/UI — `--ui-smoke`.
4. Если затронуты захват/детекция/клипы/датасет — `--smoke` (нужны go2rtc и камера).
5. Если менялось поведение — обновить `README.md`.
6. Не коммитить `models/`, `.tools/`, `bin/`, `obj/`, `settings.json`, `*.db`.

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

## 9. Git

- Коммиты — атомарные, сообщения на английском в повелительном наклонении
  (`Fix ...`, `Add ...`, `Shorten ...`).
- Push — только по явной просьбе пользователя.
- Перед коммитом проверить `git status` и не добавлять сгенерированные/локальные файлы.
- Публичный репозиторий: не коммитить скриншоты с приватным видом камеры без согласия.
