using BirdCam.Domain.Entities;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

public sealed record BirdObservation(
    DateTimeOffset Timestamp,
    IReadOnlyList<DetectedBird> Birds,
    string? ImagePath,
    DatasetSample? Sample);

/// <summary>Фоновый наблюдатель: периодически проверяет кадры моделью и сохраняет находки.</summary>
public interface IBirdWatcher : IAsyncDisposable
{
    bool IsRunning { get; }

    string Status { get; }

    event EventHandler<BirdObservation>? BirdObserved;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}
