using System.Collections.ObjectModel;
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
    private readonly IGo2RtcManager _go2Rtc;
    private readonly StorageLayout _layout;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ILogger<SettingsViewModel> _logger;

    public SettingsViewModel(
        ISettingsProvider settings,
        IDetector detector,
        IFrameSource frames,
        ITelegramNotifier telegram,
        IGo2RtcManager go2Rtc,
        StorageLayout layout,
        IDialogService dialogs,
        StatusService status,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _detector = detector;
        _frames = frames;
        _telegram = telegram;
        _go2Rtc = go2Rtc;
        _layout = layout;
        _dialogs = dialogs;
        _status = status;
        _logger = logger;

        LoadFrom(settings.Current);
        RefreshAvailableModels();
    }

    public string StorageRoot => _layout.Root;

    public ObservableCollection<string> AvailableModels { get; } = [];

    public string ModelStatus => _detector.Status;

    [ObservableProperty]
    private string _rtspUrl = string.Empty;

    [ObservableProperty]
    private int _displayWidth = 1280;

    [ObservableProperty]
    private string _go2RtcPath = string.Empty;

    [ObservableProperty]
    private string _go2RtcApiUrl = "http://127.0.0.1:1984";

    [ObservableProperty]
    private bool _manageGo2Rtc = true;

    [ObservableProperty]
    private bool _autoStart = true;

    [ObservableProperty]
    private string _telegramToken = string.Empty;

    [ObservableProperty]
    private string _telegramChatId = string.Empty;

    [ObservableProperty]
    private bool _autoSendPhotos = true;

    private string _modelPath = "yolo26m.onnx";

    public string ModelPath
    {
        get => _modelPath;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            SetProperty(ref _modelPath, value.Trim());
        }
    }

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
            var previousCamera = previous.Camera ?? new CameraSettings();
            var previousDetection = previous.Detection ?? new DetectionSettings();
            var previousStorage = previous.Storage ?? new StorageSettings();

            var rtspUrl = Clean(RtspUrl);
            if (rtspUrl.Length == 0)
            {
                rtspUrl = Clean(previousCamera.RtspUrl);
            }

            if (rtspUrl.Length == 0)
            {
                rtspUrl = "rtsp://127.0.0.1:8554/c200";
            }

            var modelPath = Clean(ModelPath);
            if (modelPath.Length == 0)
            {
                modelPath = Clean(previousDetection.ModelPath);
            }

            if (modelPath.Length == 0)
            {
                modelPath = "yolo26m.onnx";
            }

            var storageRoot = Clean(previousStorage.RootPath);
            if (storageRoot.Length == 0)
            {
                storageRoot = StorageSettings.DefaultStorageRoot;
            }

            var birdClassIds = Clean(BirdClassIds);
            if (birdClassIds.Length == 0)
            {
                birdClassIds = "14";
            }

            var go2RtcApiUrl = Clean(Go2RtcApiUrl);
            if (go2RtcApiUrl.Length == 0)
            {
                go2RtcApiUrl = "http://127.0.0.1:1984";
            }

            var settings = new AppSettings
            {
                Camera = new CameraSettings
                {
                    RtspUrl = rtspUrl,
                    AutoStart = AutoStart,
                    DisplayWidth = Math.Clamp(DisplayWidth, 640, 1920),
                    Go2RtcPath = Clean(Go2RtcPath),
                    Go2RtcApiUrl = go2RtcApiUrl,
                    ManageGo2Rtc = ManageGo2Rtc
                },
                Storage = new StorageSettings { RootPath = storageRoot },
                Detection = new DetectionSettings
                {
                    AutoStart = DetectionAutoStart,
                    IntervalMs = Math.Clamp(IntervalMs, 200, 10_000),
                    ConfidenceThreshold = Math.Clamp(Confidence, 0.01, 0.99),
                    BirdClassIds = birdClassIds,
                    InputSize = Math.Clamp(InputSize, 320, 1280),
                    UseGpu = UseGpu,
                    CooldownSeconds = Math.Clamp(CooldownSeconds, 5, 3600),
                    MaxSamplesPerDay = Math.Clamp(MaxSamplesPerDay, 10, 100_000),
                    ModelPath = modelPath
                },
                Telegram = new TelegramSettings
                {
                    BotToken = Clean(TelegramToken),
                    ChatId = Clean(TelegramChatId),
                    AutoSendPhotos = AutoSendPhotos
                },
                Recording = new RecordingSettings
                {
                    ClipDurationSeconds = Math.Clamp(ClipDurationSeconds, 3, 120),
                    FfmpegPath = Clean(FfmpegPath)
                }
            };

            await _settings.SaveAsync(settings);
            await _detector.InitializeAsync();

            if (!string.Equals(previousCamera.RtspUrl, settings.Camera.RtspUrl, StringComparison.OrdinalIgnoreCase)
                && _frames.IsRunning)
            {
                await _frames.StopAsync();
                await _frames.StartAsync();
            }

            if (!string.Equals(previousStorage.RootPath, settings.Storage.RootPath, StringComparison.OrdinalIgnoreCase))
            {
                _status.Set("Настройки сохранены. Для смены папки данных перезапустите приложение.");
                return;
            }

            _status.Set("Настройки сохранены");
            RefreshAvailableModels();
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
            var go2RtcStatus = await _go2Rtc.EnsureAvailableAsync();
            if (!go2RtcStatus.Available)
            {
                _status.Error(go2RtcStatus.Message);
                return;
            }

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
    private void PickGo2Rtc()
    {
        var path = _dialogs.PickFile("go2rtc.exe|go2rtc.exe|Все файлы|*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            Go2RtcPath = path;
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

    private void RefreshAvailableModels()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in new[] { _layout.Models, Path.Combine(AppContext.BaseDirectory, "models") })
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

        if (!string.IsNullOrWhiteSpace(ModelPath))
        {
            names.Add(ModelPath.Trim());
        }

        foreach (var existing in AvailableModels.ToList())
        {
            if (!names.Contains(existing))
            {
                AvailableModels.Remove(existing);
            }
        }

        foreach (var name in names)
        {
            if (!AvailableModels.Any(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase)))
            {
                AvailableModels.Add(name);
            }
        }
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;

    private void LoadFrom(AppSettings settings)
    {
        RtspUrl = settings.Camera.RtspUrl;
        DisplayWidth = settings.Camera.DisplayWidth;
        AutoStart = settings.Camera.AutoStart;
        Go2RtcPath = settings.Camera.Go2RtcPath;
        Go2RtcApiUrl = settings.Camera.Go2RtcApiUrl;
        ManageGo2Rtc = settings.Camera.ManageGo2Rtc;

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





