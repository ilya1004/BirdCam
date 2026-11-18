using System.IO;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsProvider _settings;
    private readonly IDetector _detector;
    private readonly IFrameSource _frames;
    private readonly ITelegramNotifier _telegram;
    private readonly StorageLayout _layout;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsProvider settings,
        IDetector detector,
        IFrameSource frames,
        ITelegramNotifier telegram,
        StorageLayout layout,
        IDialogService dialogs,
        StatusService status,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _detector = detector;
        _frames = frames;
        _telegram = telegram;
        _layout = layout;
        _dialogs = dialogs;
        _status = status;
        _logger = logger;

        LoadFrom(settings.Current);
        ModelHint = BuildModelHint(layout);
    }

    public string StorageRoot => _layout.Root;

    public string ModelHint { get; }

    public string ModelStatus => _detector.Status;

    [ObservableProperty]
    private string _rtspUrl = string.Empty;

    [ObservableProperty]
    private int _displayWidth = 1280;

    [ObservableProperty]
    private bool _autoStart = true;

    [ObservableProperty]
    private string _telegramToken = string.Empty;

    [ObservableProperty]
    private string _telegramChatId = string.Empty;

    [ObservableProperty]
    private bool _autoSendPhotos = true;

    [ObservableProperty]
    private string _modelPath = "yolo11n.onnx";

    [ObservableProperty]
    private bool _useGpu = true;

    [ObservableProperty]
    private int _inputSize = 640;

    [ObservableProperty]
    private double _confidence = 0.35;

    [ObservableProperty]
    private string _birdClassIds = "14";

    [ObservableProperty]
    private int _intervalMs = 800;

    [ObservableProperty]
    private int _cooldownSeconds = 45;

    [ObservableProperty]
    private int _maxSamplesPerDay = 2000;

    [ObservableProperty]
    private bool _detectionAutoStart;

    [ObservableProperty]
    private int _clipDurationSeconds = 12;

    [ObservableProperty]
    private string _ffmpegPath = string.Empty;

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var previous = _settings.Current;
            var settings = new AppSettings
            {
                Camera = new CameraSettings
                {
                    RtspUrl = RtspUrl.Trim(),
                    AutoStart = AutoStart,
                    DisplayWidth = Math.Clamp(DisplayWidth, 640, 1920)
                },
                Storage = new StorageSettings { RootPath = previous.Storage.RootPath },
                Detection = new DetectionSettings
                {
                    AutoStart = DetectionAutoStart,
                    IntervalMs = Math.Clamp(IntervalMs, 200, 10_000),
                    ConfidenceThreshold = Math.Clamp(Confidence, 0.01, 0.99),
                    BirdClassIds = BirdClassIds.Trim(),
                    InputSize = Math.Clamp(InputSize, 320, 1280),
                    UseGpu = UseGpu,
                    CooldownSeconds = Math.Clamp(CooldownSeconds, 5, 3600),
                    MaxSamplesPerDay = Math.Clamp(MaxSamplesPerDay, 10, 100_000),
                    ModelPath = ModelPath.Trim()
                },
                Telegram = new TelegramSettings
                {
                    BotToken = TelegramToken.Trim(),
                    ChatId = TelegramChatId.Trim(),
                    AutoSendPhotos = AutoSendPhotos
                },
                Recording = new RecordingSettings
                {
                    ClipDurationSeconds = Math.Clamp(ClipDurationSeconds, 3, 120),
                    FfmpegPath = FfmpegPath.Trim()
                }
            };

            await _settings.SaveAsync(settings);
            await _detector.InitializeAsync();

            if (!string.Equals(previous.Camera.RtspUrl, settings.Camera.RtspUrl, StringComparison.OrdinalIgnoreCase)
                && _frames.IsRunning)
            {
                await _frames.StopAsync();
                await _frames.StartAsync();
            }

            if (!string.Equals(previous.Storage.RootPath, settings.Storage.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                _status.Set("Настройки сохранены. Для смены папки данных перезапустите приложение.");
                return;
            }

            _status.Set("Настройки сохранены");
            OnPropertyChanged(nameof(ModelStatus));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось сохранить настройки");
            _status.Error("Настройки: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task TestTelegramAsync()
    {
        var result = await _telegram.SendTextAsync($"BirdCam: проверка связи ✅\n{DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        _status.Set(result.Success ? "Сообщение отправлено — проверьте Telegram" : "Telegram: " + result.Error);
    }

    [RelayCommand]
    private async Task TestCameraAsync()
    {
        var wasRunning = _frames.IsRunning;
        try
        {
            if (!wasRunning)
            {
                await _frames.StartAsync();
            }

            await Task.Delay(3000);
            var hasFrame = _frames.HasFrame;

            _status.Set(hasFrame
                ? $"Камера отвечает: {_frames.Width}×{_frames.Height}, {_frames.Fps:0} fps"
                : "Камера не отвечает: " + (_frames.LastError ?? "нет кадра"));
        }
        catch (Exception exception)
        {
            _status.Error("Камера: " + exception.Message);
        }
        finally
        {
            if (!wasRunning)
            {
                await _frames.StopAsync();
            }
        }
    }

    [RelayCommand]
    private void PickFfmpeg()
    {
        var path = _dialogs.PickFile("ffmpeg.exe|ffmpeg.exe|Все файлы|*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            FfmpegPath = path;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => _dialogs.OpenFolder(_layout.Root);

    private static string BuildModelHint(StorageLayout layout)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in new[] { layout.Models, Path.Combine(AppContext.BaseDirectory, "models") })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.onnx"))
            {
                names.Add(Path.GetFileName(file));
            }
        }

        return names.Count > 0 ? "Доступны: " + string.Join(", ", names) : "Файлы .onnx не найдены";
    }

    private void LoadFrom(AppSettings settings)
    {
        RtspUrl = settings.Camera.RtspUrl;
        DisplayWidth = settings.Camera.DisplayWidth;
        AutoStart = settings.Camera.AutoStart;

        TelegramToken = settings.Telegram.BotToken;
        TelegramChatId = settings.Telegram.ChatId;
        AutoSendPhotos = settings.Telegram.AutoSendPhotos;

        ModelPath = settings.Detection.ModelPath;
        UseGpu = settings.Detection.UseGpu;
        InputSize = settings.Detection.InputSize;
        Confidence = settings.Detection.ConfidenceThreshold;
        BirdClassIds = settings.Detection.BirdClassIds;
        IntervalMs = settings.Detection.IntervalMs;
        CooldownSeconds = settings.Detection.CooldownSeconds;
        MaxSamplesPerDay = settings.Detection.MaxSamplesPerDay;
        DetectionAutoStart = settings.Detection.AutoStart;

        ClipDurationSeconds = settings.Recording.ClipDurationSeconds;
        FfmpegPath = settings.Recording.FfmpegPath;
    }
}



