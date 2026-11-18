using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using BirdCam.UI.Imaging;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI.ViewModels;

public sealed partial class DatasetViewModel : ObservableObject
{
    private readonly IDatasetService _dataset;
    private readonly IFrameSource _frames;
    private readonly IDetector _detector;
    private readonly IFileStorage _storage;
    private readonly StorageLayout _layout;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ILogger<DatasetViewModel> _logger;

    public DatasetViewModel(
        IDatasetService dataset,
        IFrameSource frames,
        IDetector detector,
        IFileStorage storage,
        StorageLayout layout,
        IDialogService dialogs,
        StatusService status,
        ILogger<DatasetViewModel> logger)
    {
        _dataset = dataset;
        _frames = frames;
        _detector = detector;
        _storage = storage;
        _layout = layout;
        _dialogs = dialogs;
        _status = status;
        _logger = logger;
        DatasetPath = layout.Dataset;
    }

    public string DatasetPath { get; }

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _today;

    [ObservableProperty]
    private string _lastSampleText = "пока нет примеров";

    [ObservableProperty]
    private ImageSource? _lastImage;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var stats = await _dataset.GetStatsAsync();
            Total = stats.Total;
            Today = stats.Today;

            if (stats.LastSampleAtUtc is null)
            {
                LastSampleText = "пока нет примеров";
                LastImage = null;
                return;
            }

            var local = stats.LastSampleAtUtc.Value.ToLocalTime();
            LastSampleText = $"последний пример: {local:dd.MM.yyyy HH:mm:ss}";

            var previewRelative = stats.LastCropPath ?? stats.LastImagePath;
            LastImage = previewRelative is null
                ? null
                : ImageLoader.Load(_storage.GetAbsolutePath(previewRelative));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось загрузить статистику датасета");
            _status.Error("Датасет: " + exception.Message);
        }
    }

    [RelayCommand]
    private void OpenFolder() => _dialogs.OpenFolder(_layout.Dataset);

    [RelayCommand]
    private async Task AddSampleAsync()
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
                _status.Set($"Добавлено: {Path.GetFileName(sample.ImagePath)} (птиц: {birds.Count})");
                await RefreshAsync();
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка добавления примера");
            _status.Error(exception.Message);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var target = _dialogs.SaveFile(
            "ZIP архив (*.zip)|*.zip",
            $"birdcam-dataset-{DateTime.Now:yyyyMMdd-HHmm}.zip");

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        try
        {
            _status.Set("Экспорт датасета…");
            await Task.Run(() => ZipFile.CreateFromDirectory(
                _layout.Dataset,
                target,
                CompressionLevel.Optimal,
                includeBaseDirectory: false));

            _status.Set("Датасет экспортирован: " + Path.GetFileName(target));
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка экспорта датасета");
            _status.Error("Экспорт: " + exception.Message);
        }
    }
}
