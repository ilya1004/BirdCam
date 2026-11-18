using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

public sealed class SnapshotService : ISnapshotService
{
    private readonly IFrameSource _frameSource;
    private readonly IFileStorage _storage;
    private readonly IMediaRepository _mediaRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SnapshotService> _logger;

    public SnapshotService(
        IFrameSource frameSource,
        IFileStorage storage,
        IMediaRepository mediaRepository,
        TimeProvider timeProvider,
        ILogger<SnapshotService> logger)
    {
        _frameSource = frameSource;
        _storage = storage;
        _mediaRepository = mediaRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<MediaItem?> CaptureAsync(string? note = null, CancellationToken cancellationToken = default)
    {
        if (!_frameSource.TryGetFrame(out var frame) || frame is null)
        {
            return null;
        }

        using (frame)
        {
            return await SaveFrameAsync(frame, note, cancellationToken);
        }
    }

    public async Task<MediaItem> SaveFrameAsync(VideoFrame frame, string? note = null, CancellationToken cancellationToken = default)
    {
        var localNow = _timeProvider.GetLocalNow();
        var fileName = $"snap_{localNow:yyyyMMdd_HHmmss_fff}.jpg";

        var relativePath = await _storage.SaveJpegAsync(frame, StoragePaths.Snapshots, fileName, cancellationToken);

        var item = new MediaItem
        {
            Kind = MediaKind.Snapshot,
            FilePath = relativePath,
            Width = frame.Width,
            Height = frame.Height,
            SizeBytes = _storage.GetFileSize(relativePath),
            CreatedAtUtc = _timeProvider.GetUtcNow(),
            Note = note
        };

        await _mediaRepository.AddAsync(item, cancellationToken);
        _logger.LogInformation("Снимок сохранён: {Path}", relativePath);

        return item;
    }
}
