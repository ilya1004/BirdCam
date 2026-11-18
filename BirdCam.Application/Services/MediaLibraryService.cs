using BirdCam.Application.Abstractions;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

public sealed class MediaLibraryService
{
    private readonly IMediaRepository _repository;
    private readonly IFileStorage _storage;
    private readonly ILogger<MediaLibraryService> _logger;

    public MediaLibraryService(IMediaRepository repository, IFileStorage storage, ILogger<MediaLibraryService> logger)
    {
        _repository = repository;
        _storage = storage;
        _logger = logger;
    }

    public Task<IReadOnlyList<MediaItem>> GetRecentAsync(
        int limit = 300,
        MediaKind? kind = null,
        CancellationToken cancellationToken = default)
        => _repository.GetRecentAsync(limit, kind, cancellationToken);

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var item = await _repository.GetAsync(id, cancellationToken);
        if (item is null)
        {
            return false;
        }

        TryDelete(item.FilePath);
        if (!string.IsNullOrWhiteSpace(item.ThumbnailPath))
        {
            TryDelete(item.ThumbnailPath);
        }

        await _repository.DeleteAsync(id, cancellationToken);
        return true;
    }

    public Task MarkSentAsync(Guid id, CancellationToken cancellationToken = default)
        => _repository.MarkSentAsync(id, DateTimeOffset.UtcNow, cancellationToken);

    private void TryDelete(string relativePath)
    {
        try
        {
            if (_storage.Exists(relativePath))
            {
                _storage.Delete(relativePath);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось удалить файл {Path}", relativePath);
        }
    }
}
