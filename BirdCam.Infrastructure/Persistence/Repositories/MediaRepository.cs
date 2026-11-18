using BirdCam.Application.Abstractions;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace BirdCam.Infrastructure.Persistence.Repositories;

public sealed class MediaRepository(IDbContextFactory<BirdCamDbContext> contextFactory) : IMediaRepository
{
    public async Task AddAsync(MediaItem item, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.MediaItems.Add(item);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MediaItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.MediaItems.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MediaItem>> GetRecentAsync(
        int limit = 300,
        MediaKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.MediaItems.AsNoTracking();
        if (kind is not null)
        {
            query = query.Where(item => item.Kind == kind);
        }

        return await query
            .OrderByDescending(item => item.CreatedAtUtc)
            .Take(Math.Clamp(limit, 1, 2000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkSentAsync(Guid id, DateTimeOffset sentAtUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.MediaItems
            .Where(item => item.Id == id)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.SentToTelegramAtUtc, sentAtUtc),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.MediaItems
            .Where(item => item.Id == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountAsync(MediaKind? kind = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = db.MediaItems.AsNoTracking();
        if (kind is not null)
        {
            query = query.Where(item => item.Kind == kind);
        }

        return await query.CountAsync(cancellationToken).ConfigureAwait(false);
    }
}
