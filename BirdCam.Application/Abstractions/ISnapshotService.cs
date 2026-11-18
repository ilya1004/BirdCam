using BirdCam.Domain.Entities;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

public interface ISnapshotService
{
    /// <summary>Сохраняет текущий кадр с камеры. Возвращает null, если кадра нет.</summary>
    Task<MediaItem?> CaptureAsync(string? note = null, CancellationToken cancellationToken = default);

    /// <summary>Сохраняет переданный кадр как снимок.</summary>
    Task<MediaItem> SaveFrameAsync(VideoFrame frame, string? note = null, CancellationToken cancellationToken = default);
}
