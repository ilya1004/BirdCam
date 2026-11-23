# Обучение модели вида

BirdCam умеет дообучать YOLO на датасете, который сам же собирает. В приложении
есть вкладка **«Обучение»**: она проверяет разметку, делит кадры на train/val,
запускает [ultralytics](https://docs.ultralytics.com/) в отдельном процессе Python,
показывает прогресс и устанавливает готовый `best.onnx` в папку моделей.

> Важно: сейчас это **детектор одного вида**. Разметка, которую собирает BirdCam,
> всегда пишет класс `0 = bird`. Вкладка «Обучение» даёт этому классу имя
> (например, «синица»), и модель учится находить синицу. Отличить синицу от
> воробья такая модель не сможет — для этого нужны примеры других видов и
> многоклассовая разметка (см. «Что дальше»).

## Что уже есть в датасете

На вашем ПК на момент написания:

- 411 кадров `1920×1080` и 411 файлов разметки;
- 426 рамок, все с классом `0`;
- 408 кропов птиц в `dataset\crops`;
- один пустой файл разметки — он пропускается при обучении;
- данные за 22–23 ноября 2026.

Пути:

```
%LOCALAPPDATA%\BirdCam\dataset\
├── data.yaml          # генерируется вкладкой «Обучение»
├── train.txt          # список кадров train (относительные пути)
├── val.txt            # список кадров val
├── images\            # полные кадры
├── labels\            # YOLO-разметка: class cx cy w h
└── crops\             # кропы для будущего классификатора видов

%LOCALAPPDATA%\BirdCam\training\
├── train.py           # скрипт обучения
├── runs\              # результаты запусков, best.pt / best.onnx
└── logs\              # журналы
```

## Вариант 1: обучение прямо в приложении (рекомендую)

1. Запустите BirdCam и откройте вкладку **«Обучение»**.
2. В блоке **«1 · Данные»** проверьте цифры и нажмите **«Проверить данные»**.
   В поле «Названия классов» должно быть `синица` (по одному названию на строку).
3. Нажмите **«Подготовить сплит»**. Приложение:
   - проверит каждую строку разметки;
   - отсортирует кадры по времени и отдаст последние 20 % в валидацию;
   - запишет `train.txt`, `val.txt` и `data.yaml`;
   - покажет, сколько кадров попало в train/val.
4. В блоке **«3 · Параметры»** выберите базовую модель и параметры:
   - `yolo26n.pt` — быстрый старт;
   - эпох `40` (можно 60–80, если качество не устроит);
   - размер входа `640`;
   - batch `8` для CPU или `16` для GPU;
   - workers `2`;
   - устройство: `0` — GPU (AMD ROCm или NVIDIA CUDA), `cpu` — без видеокарты.
     Если GPU доступна, приложение подставит `0` автоматически.
5. Нажмите **«Начать обучение»** и следите за журналом и метриками
   `mAP50`, `mAP50-95`, `P`, `R`. Кнопка **«Остановить»** завершает процесс.
6. Когда появится результат, нажмите **«Установить и использовать»**:
   - `best.onnx` скопируется в `%LOCALAPPDATA%\BirdCam\models`;
   - модель станет активной;
   - «Классы птиц» автоматически станут `0`.

Если модель уже обучена раньше, нажмите **«Выбрать готовый ONNX…»** и укажите
`best.onnx` из `training\runs\...\weights` или скачанный из Colab — установка
пройдёт без повторного обучения.

После установки проверьте «Настройки»: модель — новый файл, классы птиц — `0`.
В прямом эфире рамка будет подписана `синица`, в Telegram придёт
«Синица у кормушки». Если позже вернётесь на COCO-модель `yolo26m`, поставьте
«Классы птиц» = `14`.

### Сколько это занимает

На этом ПК (Ryzen 7 7700, 16 потоков) один прогон 328 кадров при `imgsz=640`:

| Окружение | Первая эпоха | Следующие эпохи | 40 эпох |
|---|---|---|---|
| CPU, PyTorch CPU | ~42 с | ~42 с | ~30–35 мин |
| GPU, RX 7800 XT + ROCm 10.2 | ~60 с (MIOpen warmup) | ~7–8 с | ~6–8 мин |

При `imgsz=320` эпоха на GPU занимает ещё меньше, но качество на мелких птицах
ниже. GPU-обучение проверено на Windows 11 с Adrenalin 32.0.32029.2010,
Python 3.13, PyTorch 2.15.0+rocm10.2.0 и ultralytics 8.4.216. DirectML для
обучения не используется — нужен именно ROCm-сборка PyTorch.

> На Windows/ROCm автоматический AMP отключён в `train.py`: с включённым AMP
> MIOpen в этой связке даёт сломанные метрики (mAP падает до ~0.17), хотя loss
> выглядит нормально. Без AMP обучение идёт в FP32 и метрики корректные.

## GPU AMD (ROCm) на Windows

Официальная инструкция AMD:
[Install PyTorch for ROCm](https://rocm.docs.amd.com/projects/ai-ecosystem/en/latest/frameworks/pytorch/install.html)
и [TheRock releases](https://github.com/ROCm/TheRock/blob/main/RELEASES.md).
Для RX 7800 XT нужен `gfx1101`, extra `device-gfx1101`.

Требования: Windows 11, свежий Adrenalin, 64-битный Python 3.13, ~2 ГБ на диске.
Ставить лучше в отдельное окружение, не ломая CPU-venv:

```powershell
cd C:\Users\ilyar\Projects\BirdCam
py -3.13 -m venv .tools\rocm-venv
.\.tools\rocm-venv\Scripts\python.exe -m pip install --upgrade pip

# PyTorch + ROCm 10.2 (около 1.5 ГБ)
.\.tools\rocm-venv\Scripts\python.exe -m pip install `
  --index-url https://stable.repo.amd.com/rocm/whl-next/ `
  "torch[device-gfx1101]" "torchvision[device-gfx1101]"

# ultralytics и экспорт
.\.tools\rocm-venv\Scripts\python.exe -m pip install ultralytics onnx onnxslim onnxruntime
```

Проверка:

```powershell
.\.tools\rocm-venv\Scripts\python.exe -c `
  "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0), torch.version.hip)"
```

Ожидаемо: `2.15.0+rocm10.2.0 True AMD Radeon RX 7800 XT ...`.

Дальше в BirdCam откройте **«Обучение»** и нажмите **«Проверить»**. Приложение
само найдёт `.tools\rocm-venv` (или укажите путь к `python.exe` вручную),
покажет `GPU: AMD Radeon RX 7800 XT` и подставит устройство `0`. Запускайте
обучение как обычно.

Если GPU не видна:

- проверьте, что установлен именно `torch[...]` из индекса AMD, а не CPU-сборка
  с PyPI: `python -m pip show torch` должен содержать `+rocm10.2.0`;
- обновите драйвер Adrenalin;
- `torch.cuda.device_count()` может вернуть 2 (встройка + дискретная); BirdCam
  использует `device=0`, это RX 7800 XT. При необходимости задайте
  `HIP_VISIBLE_DEVICES=0`;
- предупреждения MIOpen в первую эпоху — нормальная инициализация, не ошибка.

## Вариант 2: вручную, ultralytics на этом ПК

Окружение уже есть в `.tools\venv` (Python 3.13, ultralytics 8.4, torch CPU).
Для GPU используйте `.tools\rocm-venv` (см. раздел «GPU AMD»). Если окружения
нет:

```powershell
cd C:\Users\ilyar\Projects\BirdCam
py -3.13 -m venv .tools\venv
.\.tools\venv\Scripts\python.exe -m pip install --upgrade pip
.\.tools\venv\Scripts\python.exe -m pip install ultralytics onnx onnxslim onnxruntime
```

Подготовьте сплит в приложении (кнопка «Подготовить сплит») — или вручную
создайте `train.txt`/`val.txt` и `data.yaml`. Затем:

```powershell
cd $env:LOCALAPPDATA\BirdCam\dataset

& "C:\Users\ilyar\Projects\BirdCam\.tools\venv\Scripts\yolo.exe" detect train `
  data=data.yaml `
  model=C:\Users\ilyar\Projects\BirdCam\models\yolo26\yolo26n.pt `
  epochs=40 imgsz=640 batch=8 device=cpu workers=2 `
  project=$env:LOCALAPPDATA\BirdCam\training\runs name=manual

& "C:\Users\ilyar\Projects\BirdCam\.tools\venv\Scripts\yolo.exe" export `
  model="$env:LOCALAPPDATA\BirdCam\training\runs\manual\weights\best.pt" `
  format=onnx opset=18 imgsz=640
```

Скопируйте `best.onnx` в `%LOCALAPPDATA%\BirdCam\models`, выберите его в
«Настройках» и укажите «Классы птиц» = `0`.

Для GPU в этой же команде замените путь к `yolo.exe` на `.tools\rocm-venv\Scripts\yolo.exe`,
а `device=cpu` — на `device=0`.

`data.yaml` намеренно **не содержит `path`**: ultralytics берёт папку самого
yaml, а `train.txt`/`val.txt` содержат относительные пути `./images/...`.
Поэтому папку датасета можно переносить и распаковывать где угодно.

## Вариант 3: Colab / облако с GPU (быстрее всего)

1. В BirdCam: **Обучение → Подготовить сплит**, затем **Датасет → Экспорт ZIP**.
   В ZIP попадут `data.yaml`, `train.txt`, `val.txt`, `images`, `labels`, `crops`.
2. Загрузите ZIP в Google Drive (или прямо в файлы Colab).
3. Откройте [Google Colab](https://colab.research.google.com/) и создайте блокнот.
   Включите GPU: **Среда выполнения → Сменить среду выполнения → T4 GPU**.
4. Ячейка установки:

```python
!pip install -q ultralytics onnx onnxslim onnxruntime
```

5. Ячейка распаковки и обучения:

```python
from google.colab import drive
drive.mount('/content/drive')

!unzip -q "/content/drive/MyDrive/birdcam-dataset.zip" -d /content/dataset

!yolo detect train \
  data=/content/dataset/data.yaml \
  model=yolo26n.pt \
  epochs=80 imgsz=640 batch=16 device=0 \
  project=/content/runs name=birdcam
```

6. Ячейка экспорта и скачивания:

```python
!yolo export model=/content/runs/birdcam/weights/best.pt \
  format=onnx opset=18 imgsz=640

from google.colab import files
files.download('/content/runs/birdcam/weights/best.onnx')
```

7. Скачанный `best.onnx` положите в `%LOCALAPPDATA%\BirdCam\models`, выберите в
   «Настройках» и поставьте «Классы птиц» = `0`.

На T4 80 эпох обычно занимают 10–25 минут — это в разы быстрее локального CPU.
Готовый блокнот: [`docs/training-colab.ipynb`](training-colab.ipynb).

## Как проверить, что модель работает

```powershell
# Проверка окружения и датасета без обучения
BirdCam.UI.exe --training-check

# Одна эпоха 320px для проверки всего тракта
BirdCam.UI.exe --training-check --training-smoke

# Обучение через тот же код, что и кнопка в UI
BirdCam.UI.exe --training-check --training-ui --epochs 1 --imgsz 320 --batch 4 --workers 0

# Установить конкретный best.onnx и переключить приложение
BirdCam.UI.exe --training-check --install "C:\...\best.onnx"
```

Результаты: `%LOCALAPPDATA%\BirdCam\logs\training-check.log` и
`training-ui.log`. Сырой вывод Python — в `training\logs\<run>.log`.

## Частые проблемы

| Симптом | Причина и решение |
|---|---|
| «Python с ultralytics не найден» | Установите Python 3.12/3.13, создайте venv и поставьте `ultralytics onnx onnxslim onnxruntime`; укажите путь к `python.exe` в блоке «Окружение». |
| `torch` есть, но обучение медленное | На CPU это нормально; для RX 7800 XT установите ROCm-сборку PyTorch (раздел «GPU AMD») и `device=0`. |
| GPU есть, но `torch.cuda.is_available()` = False | Установлена CPU-сборка torch с PyPI. Переустановите из `https://stable.repo.amd.com/rocm/whl-next/` с `[device-gfx1101]`. |
| Метрики на GPU около 0.1–0.2 mAP, хотя loss падает | Включён AMP на Windows/ROCm. BirdCam уже отключает AMP для HIP; в ручных командах добавьте `amp=False`. |
| MIOpen warnings/error при старте | Первая инициализация MIOpen на Windows; обычно не влияет. Обновите Adrenalin и ROCm-сборку. |
| `ultralytics import failed` | Пакет не установлен в выбранном Python. Проверьте путь и `pip list`. |
| `No labels found` / `class ... not found` | Нажмите «Подготовить сплит»; проверьте, что названий классов не меньше, чем максимальный класс в `labels`. |
| Метрики высокие, а на новых кадрах плохо | Валидация — последние кадры того же дня и той же сцены. Проверяйте на новом дне/другом освещении; добавьте данные. |
| Модель путает синицу с другими птицами | Это одноклассовый детектор: он не знает других видов. Нужна многоклассовая разметка и переобучение. |
| После установки ничего не детектируется | Проверьте «Классы птиц» = `0`, порог уверенности и что модель действительно новый файл. |
| `Could not find ref with POC` | Штатное предупреждение HEVC-декодера, к обучению не относится. |

## Что дальше: несколько видов

Сейчас автосбор пишет все рамки как класс `0`. Чтобы различать виды, нужно:

1. **Разметить виды.** Самый простой путь — раскладывать кропы по папкам
   `dataset\crops\синица`, `dataset\crops\воробей`, ... и обучать
   **классификатор** (EfficientNet/MobileNet → ONNX), который запускается на
   кропе, найденном детектором. Для 5–10 видов достаточно 100–300 кропов на вид.
2. **Или сделать многоклассовый детектор.** Переразметить полные кадры, где
   каждому виду соответствует свой индекс, и указать все названия в поле
   «Названия классов» (по одному на строку). BirdCam пока автоматически пишет
   только класс `0`, поэтому разметку придётся поправить вручную или отдельным
   инструментом.
3. **Накопить больше данных:** разные дни, утро/вечер, снег/дождь, ИК-ночь.
   Одна сцена и один день дают оптимистичные метрики, но плохую обобщаемость.

Кропы уже сохраняются автоматически — это задел для классификатора видов.
