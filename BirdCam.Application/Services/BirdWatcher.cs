using System.Globalization;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

/// <summary>
/// Фоновый цикл: берёт последний кадр, прогоняет через детектор и при находке
/// сохраняет пример в датасет, пишет детекции в БД и (опционально) отправляет фото в Telegram.
/// </summary>
public sealed class BirdWatcher : IBirdWatcher
{
    private readonly IFrameSource _frames;
    private readonly IDetector _detector;
    private readonly IDatasetService _dataset;
    private readonly IDatasetRepository _datasetRepository;
    private readonly IDetectionRepository _detectionRepository;
    private readonly ITelegramNotifier _telegram;
    private readonly IFileStorage _storage;
    private readonly ISettingsProvider _settings;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BirdWatcher> _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTimeOffset _lastSavedAt = DateTimeOffset.MinValue;
    private volatile string _status = "выключено";

    public BirdWatcher(
        IFrameSource frames,
        IDetector detector,
        IDatasetService dataset,
        IDatasetRepository datasetRepository,
        IDetectionRepository detectionRepository,
        ITelegramNotifier telegram,
        IFileStorage storage,
        ISettingsProvider settings,
        TimeProvider timeProvider,
        ILogger<BirdWatcher> logger)
    {
        _frames = frames;
        _detector = detector;
        _dataset = dataset;
        _datasetRepository = datasetRepository;
        _detectionRepository = detectionRepository;
        _telegram = telegram;
        _storage = storage;
        _settings = settings;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public bool IsRunning => _loop is { IsCompleted: false };

    public string Status => _status;

    public event EventHandler<BirdObservation>? BirdObserved;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
            _status = "запуск…";
            _logger.LogInformation("Наблюдение за птицами запущено");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        finally
        {
            _stateGate.Release();
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ожидаемо при остановке
            }
        }

        cts.Dispose();
        _status = "выключено";
        _logger.LogInformation("Наблюдение за птицами остановлено");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stateGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _status = "наблюдение";

        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = _settings.Current;
            var interval = Math.Clamp(settings.Detection.IntervalMs, 200, 10_000);

            try
            {
                if (!_detector.IsAvailable)
                {
                    _status = "модель недоступна";
                    await Task.Delay(5_000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!_frames.TryGetFrame(out var frame) || frame is null)
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                using (frame)
                {
                    var birds = await _detector.DetectAsync(frame, cancellationToken).ConfigureAwait(false);

                    if (birds.Count == 0)
                    {
                        _status = "птиц нет";
                    }
                    else
                    {
                        _status = $"птица: {birds.Max(bird => bird.Confidence).ToString("P0", CultureInfo.CurrentCulture)}";

                        var now = _timeProvider.GetUtcNow();
                        var cooldown = TimeSpan.FromSeconds(Math.Max(1, settings.Detection.CooldownSeconds));

                        if (now - _lastSavedAt >= cooldown)
                        {
                            _lastSavedAt = now;
                            await HandleObservationAsync(frame, birds, settings, now, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _status = "ошибка детекции";
                _logger.LogWarning(exception, "Ошибка в цикле наблюдения");
            }

            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleObservationAsync(
        VideoFrame frame,
        IReadOnlyList<DetectedBird> birds,
        AppSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var localNow = now.ToLocalTime();
        var startOfDay = new DateTimeOffset(localNow.Date, localNow.Offset).ToUniversalTime();
        var samplesToday = await _datasetRepository.CountSinceAsync(startOfDay, cancellationToken).ConfigureAwait(false);

        if (samplesToday >= settings.Detection.MaxSamplesPerDay)
        {
            _status = "дневной лимит датасета";
            return;
        }

        var sample = await _dataset.AddAsync(frame, birds, DatasetSource.Auto, cancellationToken).ConfigureAwait(false);

        var records = birds
            .Select(bird => new DetectionRecord
            {
                DatasetSampleId = sample.Id,
                Label = bird.Label,
                Confidence = bird.Confidence,
                X = bird.Box.X,
                Y = bird.Box.Y,
                Width = bird.Box.Width,
                Height = bird.Box.Height,
                CreatedAtUtc = now
            })
            .ToList();

        await _detectionRepository.AddRangeAsync(records, cancellationToken).ConfigureAwait(false);

        var imagePath = _storage.GetAbsolutePath(sample.CropPath ?? sample.ImagePath);

        if (settings.Telegram.AutoSendPhotos && _telegram.IsConfigured)
        {
            var result = await _telegram
                .SendPhotoAsync(imagePath, BuildCaption(birds, localNow), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning("Telegram: {Error}", result.Error);
            }
        }

        _status = $"сохранено: {Path.GetFileName(sample.ImagePath)}";
        BirdObserved?.Invoke(this, new BirdObservation(now, birds, imagePath, sample));
    }

    private static string BuildCaption(IReadOnlyList<DetectedBird> birds, DateTimeOffset localNow)
    {
        var best = birds.Max(bird => bird.Confidence);
        var count = birds.Count == 1 ? "1 птица" : $"{birds.Count} объектов";
        return string.Create(
            CultureInfo.CurrentCulture,
            $"🐦 Птица у кормушки\nУверенность: {best:P0}\n{count}\n{localNow:dd.MM.yyyy HH:mm:ss}");
    }
}
