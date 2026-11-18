using BirdCam.Domain.Entities;

namespace BirdCam.Application.Abstractions;

public interface IDatasetRepository
{
    Task AddAsync(DatasetSample sample, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    Task<int> CountSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);

    Task<DatasetSample?> GetLatestAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatasetSample>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default);
}
