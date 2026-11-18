using BirdCam.Application.Abstractions;
using BirdCam.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BirdCam.Infrastructure.Persistence.Repositories;

public sealed class DatasetRepository(IDbContextFactory<BirdCamDbContext> contextFactory) : IDatasetRepository
{
    public async Task AddAsync(DatasetSample sample, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.DatasetSamples.Add(sample);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DatasetSamples.AsNoTracking().CountAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DatasetSamples.AsNoTracking()
            .CountAsync(sample => sample.CreatedAtUtc >= sinceUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<DatasetSample?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DatasetSamples.AsNoTracking()
            .OrderByDescending(sample => sample.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DatasetSample>> GetRecentAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.DatasetSamples.AsNoTracking()
            .OrderByDescending(sample => sample.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
