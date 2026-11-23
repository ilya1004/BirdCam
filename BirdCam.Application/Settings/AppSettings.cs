using System.Text.Json;

namespace BirdCam.Application.Settings;

/// <summary>
/// Пользовательские настройки приложения. Хранятся в settings.json в %LOCALAPPDATA%\BirdCam.
/// </summary>
public sealed class AppSettings
{
    public CameraSettings Camera { get; set; } = new();

    public StorageSettings Storage { get; set; } = new();

    public DetectionSettings Detection { get; set; } = new();

    public TelegramSettings Telegram { get; set; } = new();

    public RecordingSettings Recording { get; set; } = new();

    public TrainingSettings Training { get; set; } = new();

    public AppSettings Clone()
        => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this)) ?? new AppSettings();
}

public sealed class CameraSettings
{
    public string RtspUrl { get; set; } = "rtsp://127.0.0.1:8554/c200";

    public bool AutoStart { get; set; } = true;

    /// <summary>Ширина кадра для отображения (полный кадр используется для снимков и детекции).</summary>
    public int DisplayWidth { get; set; } = 1280;

    /// <summary>Путь к go2rtc.exe. Пусто — автопоиск в PATH и типовых папках.</summary>
    public string Go2RtcPath { get; set; } = string.Empty;

    /// <summary>HTTP API go2rtc для проверки доступности.</summary>
    public string Go2RtcApiUrl { get; set; } = "http://127.0.0.1:1984";

    /// <summary>Запускать go2rtc вместе с приложением и останавливать при выходе.</summary>
    public bool ManageGo2Rtc { get; set; } = true;
}

public sealed class StorageSettings
{
    public string RootPath { get; set; } = DefaultStorageRoot;

    public static string DefaultStorageRoot
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirdCam");
}

public sealed class DetectionSettings
{
    public bool AutoStart { get; set; } = true;

    public int IntervalMs { get; set; } = 800;

    public double ConfidenceThreshold { get; set; } = 0.35;

    /// <summary>Индексы классов модели, которые считаются птицами. Для COCO-модели это 14.</summary>
    public string BirdClassIds { get; set; } = "14";

    public int InputSize { get; set; } = 640;

    public bool UseGpu { get; set; } = true;

    public int CooldownSeconds { get; set; } = 45;

    public int MaxSamplesPerDay { get; set; } = 2000;

    /// <summary>Имя файла модели в папке models. Можно указать абсолютный путь.</summary>
    public string ModelPath { get; set; } = "yolo26m.onnx";

    public int[] ParseClassIds()
    {
        var ids = BirdClassIds
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var id) ? id : -1)
            .Where(id => id >= 0)
            .Distinct()
            .ToArray();

        return ids.Length > 0 ? ids : [14];
    }
}

public sealed class TelegramSettings
{
    public string BotToken { get; set; } = string.Empty;

    public string ChatId { get; set; } = string.Empty;

    public bool AutoSendPhotos { get; set; } = true;
}

public sealed class RecordingSettings
{
    public int ClipDurationSeconds { get; set; } = 12;

    /// <summary>Путь к ffmpeg.exe. Если пусто — ищется в PATH и папке winget.</summary>
    public string FfmpegPath { get; set; } = string.Empty;
}

public sealed class TrainingSettings
{
    /// <summary>Python с ultralytics. Пусто — автопоиск (.tools/venv, training/venv, PATH, py launcher).</summary>
    public string PythonPath { get; set; } = string.Empty;

    /// <summary>Названия классов по индексу, по одному на строку. Для текущего датасета — «синица».</summary>
    public string ClassNames { get; set; } = "синица";

    /// <summary>Доля кадров для валидации, %. Сплит делается по времени: последние кадры уходят в val.</summary>
    public int ValidationPercent { get; set; } = 20;

    /// <summary>Базовая .pt-модель: имя для автозагрузки ultralytics или абсолютный путь.</summary>
    public string BaseModel { get; set; } = "yolo26n.pt";

    public int Epochs { get; set; } = 40;

    public int ImageSize { get; set; } = 640;

    public int Batch { get; set; } = 8;

    public int Workers { get; set; } = 2;

    /// <summary>Устройство обучения: cpu или индекс CUDA (например, 0).</summary>
    public string Device { get; set; } = "cpu";

    /// <summary>Экспортировать best.onnx сразу после обучения.</summary>
    public bool ExportOnnx { get; set; } = true;
}



