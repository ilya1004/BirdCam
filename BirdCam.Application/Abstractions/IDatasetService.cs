using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

public sealed record DatasetStats(
    int Total,
    int Today,
    DateTimeOffset? LastSampleAtUtc,
    string? LastImagePath,
    string? LastCropPath);

public interface IDatasetService
{
    Task<DatasetSample> AddAsync(
        VideoFrame frame,
        IReadOnlyList<DetectedBird> birds,
        DatasetSource source,
        CancellationToken cancellationToken = default);

    Task<DatasetStats> GetStatsAsync(CancellationToken cancellationToken = default);
}
