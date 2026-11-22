namespace BirdCam.Application.Abstractions;

/// <summary>Результат проверки/запуска go2rtc.</summary>
public sealed record Go2RtcStatus(bool Available, bool StartedByApp, string Message)
{
    public static Go2RtcStatus Unavailable(string message) => new(false, false, message);
}

/// <summary>Управление локальным go2rtc: проверка доступности, запуск и остановка.</summary>
public interface IGo2RtcManager
{
    bool IsManagedProcessRunning { get; }

    Task<Go2RtcStatus> CheckAsync(CancellationToken cancellationToken = default);

    Task<Go2RtcStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Останавливает только тот go2rtc, который запустило приложение.</summary>
    Task StopManagedAsync(CancellationToken cancellationToken = default);
}
