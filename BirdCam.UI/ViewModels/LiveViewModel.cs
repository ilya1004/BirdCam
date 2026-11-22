using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using BirdCam.UI.Imaging;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI.ViewModels;

public sealed partial class LiveViewModel : ObservableObject, IDisposable
{
    private readonly IFrameSource _frames;
    private readonly ISnapshotService _snapshots;
    private readonly IClipService _clips;
    private readonly IBirdWatcher _watcher;
    private readonly IDetector _detector;
    private readonly IDatasetService _dataset;
    private readonly ISettingsProvider _settings;
    private readonly IFileStorage _storage;
    private readonly ITelegramNotifier _telegram;
    private readonly IDetectionRepository _detectionRepository;
    private readonly IGo2RtcManager _go2Rtc;
    private readonly StatusService _status;
    private readonly ILogger<LiveViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _overlayTimer;
    private readonly DispatcherTimer _go2RtcTimer;

    private WriteableBitmap? _bitmap;
    private DateTime _lastGo2RtcStartAttempt = DateTime.MinValue;
    private bool _go2RtcCheckInProgress;
    private bool _go2RtcErrorReported;

    public LiveViewModel(
        IFrameSource frames,
        ISnapshotService snapshots,
        IClipService clips,
        IBirdWatcher watcher,
        IDetector detector,
        IDatasetService dataset,
        ISettingsProvider settings,
        IFileStorage storage,
        ITelegramNotifier telegram,
        IDetectionRepository detections,
        IGo2RtcManager go2Rtc,
        StatusService status,
        ILogger<LiveViewModel> logger)
    {
        _frames = frames;
        _snapshots = snapshots;
        _clips = clips;
        _watcher = watcher;
        _detector = detector;
        _dataset = dataset;
        _settings = settings;
        _storage = storage;
        _telegram = telegram;
        _detectionRepository = detections;
        _go2Rtc = go2Rtc;
        _status = status;
        _logger = logger;

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(70)
        };
        _timer.Tick += OnFrameTick;

        _overlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _overlayTimer.Tick += (_, _) =>
        {
            _overlayTimer.Stop();
            Detections = [];
        };

        _go2RtcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _go2RtcTimer.Tick += async (_, _) => await CheckGo2RtcAsync();
        _go2RtcTimer.Start();

        _watcher.BirdObserved += OnBirdObserved;

        IsRunning = _frames.IsRunning;
        IsDetectionEnabled = _watcher.IsRunning;

        if (IsRunning)
        {
            _timer.Start();
        }

        _ = RefreshTodayAsync();
        _ = CheckGo2RtcAsync();
    }

    public event EventHandler? MediaAdded;

    public event EventHandler? DatasetAdded;

    [ObservableProperty]
    private ImageSource? _frame;

    [ObservableProperty]
    private double _sourceWidth;

    [ObservableProperty]
    private double _sourceHeight;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isDetectionEnabled;

    [ObservableProperty]
    private string _resolutionText = "поток не запущен";

    [ObservableProperty]
    private string _fpsText = string.Empty;

    [ObservableProperty]
    private string _detectionStatus = "выключено";

    [ObservableProperty]
    private string _lastEventText = "событий пока не было";

    [ObservableProperty]
    private int _todayBirds;

    [ObservableProperty]
    private DetectedBird[] _detections = [];

    [ObservableProperty]
    private string _go2RtcStatusText = "go2rtc: проверка…";

    [ObservableProperty]
    private bool _isGo2RtcAvailable;

    [ObservableProperty]
    private string? _lastSnapshotPath;

    [ObservableProperty]
    private ImageSource? _lastSnapshot;

    [RelayCommand]
    private async Task StartAsync()
    {
        try
        {
            var go2RtcStatus = await _go2Rtc.EnsureAvailableAsync();
            ApplyGo2RtcStatus(go2RtcStatus);

            if (!go2RtcStatus.Available)
            {
                _status.Error(go2RtcStatus.Message);
                return;
            }

            await _frames.StartAsync();
            _timer.Start();
            IsRunning = true;
            ResolutionText = "подключение…";
            _status.Set("Поток запущен");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось запустить поток");
            _status.Error("Не удалось запустить поток: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            _timer.Stop();
            await _frames.StopAsync();
            IsRunning = false;
            Frame = null;
            _bitmap = null;
            ResolutionText = "поток остановлен";
            FpsText = string.Empty;
            _status.Set("Поток остановлен");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось остановить поток");
            _status.Error(exception.Message);
        }
    }

    [RelayCommand]
    private async Task SnapshotAsync()
    {
        try
        {
            var item = await _snapshots.CaptureAsync("manual");
            if (item is null)
            {
                _status.Error("Нет кадра: включите поток");
                return;
            }

            LastSnapshotPath = _storage.GetAbsolutePath(item.FilePath);
            LastSnapshot = ImageLoader.Load(LastSnapshotPath);
            _status.Set($"Снимок сохранён: {Path.GetFileName(item.FilePath)}");
            MediaAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка снимка");
            _status.Error("Не удалось сохранить снимок: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task RecordClipAsync()
    {
        try
        {
            var seconds = Math.Clamp(_settings.Current.Recording.ClipDurationSeconds, 3, 120);
            _status.Set($"Запись клипа: {seconds} с…");
            var item = await _clips.RecordAsync(TimeSpan.FromSeconds(seconds));
            _status.Set($"Клип сохранён: {Path.GetFileName(item.FilePath)} ({item.DurationSeconds:0.#} с)");
            MediaAdded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка записи клипа");
            _status.Error("Не удалось записать клип: " + exception.Message);
        }
    }

    [RelayCommand]
    private async Task AddToDatasetAsync()
    {
        if (!_frames.TryGetFrame(out var frame) || frame is null)
        {
            _status.Error("Нет кадра: включите поток");
            return;
        }

        try
        {
            using (frame)
            {
                var birds = _detector.IsAvailable
                    ? await _detector.DetectAsync(frame)
                    : Array.Empty<DetectedBird>();

                var sample = await _dataset.AddAsync(frame, birds, DatasetSource.Manual);
                _status.Set($"Добавлено в датасет: {Path.GetFileName(sample.ImagePath)} (птиц: {birds.Count})");
                DatasetAdded?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка добавления в датасет");
            _status.Error(exception.Message);
        }
    }

    [RelayCommand]
    private async Task SendLastAsync()
    {
        try
        {
            var path = LastSnapshotPath;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                var item = await _snapshots.CaptureAsync("telegram");
                if (item is null)
                {
                    _status.Error("Нет кадра: включите поток");
                    return;
                }

                path = _storage.GetAbsolutePath(item.FilePath);
                LastSnapshotPath = path;
                LastSnapshot = ImageLoader.Load(path);
            }

            var result = await _telegram.SendPhotoAsync(path, $"BirdCam · {DateTime.Now:dd.MM.yyyy HH:mm}");
            _status.Set(result.Success ? "Фото отправлено в Telegram" : "Telegram: " + result.Error);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка отправки в Telegram");
            _status.Error(exception.Message);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _overlayTimer.Stop();
        _go2RtcTimer.Stop();
        _watcher.BirdObserved -= OnBirdObserved;
    }

    partial void OnIsDetectionEnabledChanged(bool value)
    {
        if (value)
        {
            _ = _watcher.StartAsync();
            _status.Set("Автодетекция включена");
        }
        else
        {
            _ = _watcher.StopAsync();
            _status.Set("Автодетекция выключена");
        }
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        DetectionStatus = _watcher.IsRunning ? _watcher.Status : "выключено";
        FpsText = _frames.Fps > 0 ? $"{_frames.Fps:0} fps" : string.Empty;

        if (!_frames.TryGetFrame(out var frame, _settings.Current.Camera.DisplayWidth) || frame is null)
        {
            if (!string.IsNullOrWhiteSpace(_frames.LastError))
            {
                ResolutionText = _frames.LastError;
            }

            return;
        }

        using (frame)
        {
            if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
            {
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr24, null);
                Frame = _bitmap;
                SourceWidth = frame.Width;
                SourceHeight = frame.Height;
            }

            _bitmap.WritePixels(
                new Int32Rect(0, 0, frame.Width, frame.Height),
                frame.Pixels,
                frame.Stride,
                0);

            if (!ReferenceEquals(Frame, _bitmap))
            {
                Frame = _bitmap;
            }

            ResolutionText = $"{_frames.Width}×{_frames.Height}";
        }
    }

    private void OnBirdObserved(object? sender, BirdObservation observation)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(new Action(() =>
        {
            Detections = observation.Birds.ToArray();
            LastEventText = $"{observation.Timestamp.ToLocalTime():HH:mm:ss} · птиц: {observation.Birds.Count}";
            LastSnapshotPath = observation.ImagePath;
            LastSnapshot = ImageLoader.Load(observation.ImagePath);
            TodayBirds += observation.Birds.Count;

            _overlayTimer.Stop();
            _overlayTimer.Start();

            MediaAdded?.Invoke(this, EventArgs.Empty);
            DatasetAdded?.Invoke(this, EventArgs.Empty);
        }));
    }

    private async Task CheckGo2RtcAsync()
    {
        if (_go2RtcCheckInProgress)
        {
            return;
        }

        _go2RtcCheckInProgress = true;
        try
        {
            Go2RtcStatus status;
            if (DateTime.UtcNow - _lastGo2RtcStartAttempt > TimeSpan.FromSeconds(15))
            {
                _lastGo2RtcStartAttempt = DateTime.UtcNow;
                status = await _go2Rtc.EnsureAvailableAsync();
            }
            else
            {
                status = await _go2Rtc.CheckAsync();
            }

            ApplyGo2RtcStatus(status);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Ошибка проверки доступности go2rtc");
        }
        finally
        {
            _go2RtcCheckInProgress = false;
        }
    }

    private void ApplyGo2RtcStatus(Go2RtcStatus status)
    {
        IsGo2RtcAvailable = status.Available;
        Go2RtcStatusText = status.Available ? "go2rtc: работает" : "go2rtc: недоступен";

        if (!status.Available && !_go2RtcErrorReported)
        {
            _go2RtcErrorReported = true;
            _status.Error(status.Message);
        }
        else if (status.Available && _go2RtcErrorReported)
        {
            _go2RtcErrorReported = false;
            _status.Set(status.Message);
        }
    }

    private async Task RefreshTodayAsync()
    {
        try
        {
            var localNow = DateTimeOffset.Now;
            var startOfDay = new DateTimeOffset(localNow.Date, localNow.Offset).ToUniversalTime();
            TodayBirds = await _detectionRepository.CountSinceAsync(startOfDay);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось получить статистику за сегодня");
        }
    }
}




