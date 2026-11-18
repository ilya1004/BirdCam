using BirdCam.Domain.Entities;

namespace BirdCam.Application.Abstractions;

public interface IDetectionRepository
{
    Task AddRangeAsync(IReadOnlyList<DetectionRecord> detections, CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DetectionRecord>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default);
}
