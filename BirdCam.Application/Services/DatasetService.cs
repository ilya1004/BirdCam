using BirdCam.Application.Abstractions;
using BirdCam.Application.Detection;
using BirdCam.Application.Storage;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

public sealed class DatasetService : IDatasetService
{
    private readonly IFileStorage _storage;
    private readonly IDatasetRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DatasetService> _logger;

    public DatasetService(
        IFileStorage storage,
        IDatasetRepository repository,
        TimeProvider timeProvider,
        ILogger<DatasetService> logger)
    {
        _storage = storage;
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DatasetSample> AddAsync(
        VideoFrame frame,
        IReadOnlyList<DetectedBird> birds,
        DatasetSource source,
        CancellationToken cancellationToken = default)
    {
        await EnsureDescriptorAsync(cancellationToken);

        var localNow = _timeProvider.GetLocalNow();
        var baseName = $"{localNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..6]}";

        var imagePath = await _storage.SaveJpegAsync(frame, StoragePaths.DatasetImages, baseName + ".jpg", cancellationToken);
        var labelPath = await _storage.SaveTextAsync(
            StoragePaths.DatasetLabels,
            baseName + ".txt",
            YoloLabels.Write(frame.Width, frame.Height, birds),
            cancellationToken);

        string? cropPath = null;
        if (birds.Count > 0)
        {
            var best = birds.OrderByDescending(bird => bird.Confidence).First();
            cropPath = await _storage.SaveJpegRegionAsync(
                frame,
                best.Box.Expand(0.15),
                StoragePaths.DatasetCrops,
                baseName + "_crop.jpg",
                cancellationToken);
        }

        var sample = new DatasetSample
        {
            ImagePath = imagePath,
            LabelPath = labelPath,
            CropPath = cropPath,
            BirdCount = birds.Count,
            MaxConfidence = birds.Count > 0 ? birds.Max(bird => bird.Confidence) : 0d,
            Source = source,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _repository.AddAsync(sample, cancellationToken);
        _logger.LogInformation(
            "Пример датасета сохранён: {Image}, птиц: {Count}, источник: {Source}",
            sample.ImagePath,
            sample.BirdCount,
            source);

        return sample;
    }

    public async Task<DatasetStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var total = await _repository.CountAsync(cancellationToken);

        var localNow = _timeProvider.GetLocalNow();
        var startOfDay = new DateTimeOffset(localNow.Date, localNow.Offset).ToUniversalTime();
        var today = await _repository.CountSinceAsync(startOfDay, cancellationToken);

        var latest = await _repository.GetLatestAsync(cancellationToken);

        return new DatasetStats(total, today, latest?.CreatedAtUtc, latest?.ImagePath, latest?.CropPath);
    }

    private async Task EnsureDescriptorAsync(CancellationToken cancellationToken)
    {
        const string descriptor = StoragePaths.Dataset + "/data.yaml";
        if (_storage.Exists(descriptor))
        {
            return;
        }

        await _storage.SaveTextAsync(
            StoragePaths.Dataset,
            "data.yaml",
            YoloLabels.CreateDataYaml("images", "images", ["bird"]),
            cancellationToken);
    }
}
