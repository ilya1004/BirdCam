using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;

namespace BirdCam.Application.Abstractions;

public interface IMediaRepository
{
    Task AddAsync(MediaItem item, CancellationToken cancellationToken = default);

    Task<MediaItem?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MediaItem>> GetRecentAsync(
        int limit = 300,
        MediaKind? kind = null,
        CancellationToken cancellationToken = default);

    Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<int> CountAsync(MediaKind? kind = null, CancellationToken cancellationToken = default);
}
