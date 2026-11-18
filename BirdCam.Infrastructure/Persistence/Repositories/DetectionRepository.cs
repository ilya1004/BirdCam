using BirdCam.Application.Abstractions;
using BirdCam.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BirdCam.Infrastructure.Persistence.Repositories;

public sealed class DetectionRepository(IDbContextFactory<BirdCamDbContext> contextFactory) : IDetectionRepository
{
    public async Task AddRangeAsync(IReadOnlyList<DetectionRecord> detections, CancellationToken cancellationToken = default)
    {
        if (detections.Count == 0)
        {
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.Detections.AddRange(detections);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountSinceAsync(DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Detections.AsNoTracking()
            .CountAsync(detection => detection.CreatedAtUtc >= sinceUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DetectionRecord>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Detections.AsNoTracking()
            .OrderByDescending(detection => detection.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
