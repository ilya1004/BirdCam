using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

/// <summary>Источник кадров с камеры (RTSP).</summary>
public interface IFrameSource
{
    bool IsRunning { get; }

    bool HasFrame { get; }

    string? LastError { get; }

    int Width { get; }

    int Height { get; }

    double Fps { get; }

    DateTimeOffset? LastFrameAt { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();

    /// <summary>
    /// Возвращает последний кадр. Кадр нужно освободить (using).
    /// maxWidth ограничивает ширину (для отображения), 0 — полный кадр.
    /// </summary>
    bool TryGetFrame(out VideoFrame? frame, int maxWidth = 0);
}
