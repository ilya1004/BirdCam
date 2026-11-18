namespace BirdCam.Application.Abstractions;

public sealed record TelegramSendResult(bool Success, string? Error = null)
{
    public static TelegramSendResult Ok() => new(true);

    public static TelegramSendResult Fail(string error) => new(false, error);
}

public interface ITelegramNotifier
{
    bool IsConfigured { get; }

    Task<TelegramSendResult> SendPhotoAsync(string absolutePath, string caption, CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendVideoAsync(string absolutePath, string caption, CancellationToken cancellationToken = default);

    Task<TelegramSendResult> SendTextAsync(string text, CancellationToken cancellationToken = default);
}
