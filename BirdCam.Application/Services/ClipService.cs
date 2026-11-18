using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

public sealed class ClipService : IClipService
{
    private readonly IClipRecorder _recorder;
    private readonly IFileStorage _storage;
    private readonly IMediaRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClipService> _logger;

    public ClipService(
        IClipRecorder recorder,
        IFileStorage storage,
        IMediaRepository repository,
        TimeProvider timeProvider,
        ILogger<ClipService> logger)
    {
        _recorder = recorder;
        _storage = storage;
        _repository = repository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MediaItem> RecordAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var localNow = _timeProvider.GetLocalNow();
        var fileName = $"clip_{localNow:yyyyMMdd_HHmmss}.mp4";

        _storage.EnsureDirectory(StoragePaths.Clips);
        var absolutePath = _storage.GetAbsolutePath($"{StoragePaths.Clips}/{fileName}");

        var recording = await _recorder.RecordAsync(absolutePath, duration, cancellationToken);
        var relativePath = $"{StoragePaths.Clips}/{fileName}";

        string? thumbnail = null;
        try
        {
            thumbnail = await _storage.CreateThumbnailAsync(
                relativePath,
                StoragePaths.Thumbnails,
                Path.GetFileNameWithoutExtension(fileName) + ".jpg",
                480,
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось создать превью для клипа {Path}", relativePath);
        }

        var item = new MediaItem
        {
            Kind = MediaKind.Clip,
            FilePath = relativePath,
            ThumbnailPath = thumbnail,
            Width = recording.Width,
            Height = recording.Height,
            DurationSeconds = recording.DurationSeconds,
            SizeBytes = recording.SizeBytes,
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        await _repository.AddAsync(item, cancellationToken);
        _logger.LogInformation("Клип записан: {Path} ({Duration:F1} с)", relativePath, recording.DurationSeconds);

        return item;
    }
}
