using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace BirdCam.Infrastructure.Telegram;

public sealed class TelegramBotNotifier : ITelegramNotifier
{
    private readonly ISettingsProvider _settings;
    private readonly ILogger<TelegramBotNotifier> _logger;

    public TelegramBotNotifier(ISettingsProvider settings, ILogger<TelegramBotNotifier> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsConfigured => TryGetCredentials(out _, out _);

    public async Task<TelegramSendResult> SendPhotoAsync(
        string absolutePath,
        string caption,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCredentials(out var token, out var chatId))
        {
            return TelegramSendResult.Fail("Telegram не настроен: укажите токен и chat id.");
        }

        try
        {
            await using var stream = File.OpenRead(absolutePath);
            var client = new TelegramBotClient(token);
            await client.SendPhoto(
                chatId,
                InputFile.FromStream(stream, Path.GetFileName(absolutePath)),
                caption: caption,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return TelegramSendResult.Ok();
        }
        catch (Exception exception) when (exception is ApiRequestException or RequestException or IOException)
        {
            _logger.LogWarning(exception, "Не удалось отправить фото в Telegram");
            return TelegramSendResult.Fail(exception.Message);
        }
    }

    public async Task<TelegramSendResult> SendVideoAsync(
        string absolutePath,
        string caption,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCredentials(out var token, out var chatId))
        {
            return TelegramSendResult.Fail("Telegram не настроен: укажите токен и chat id.");
        }

        try
        {
            await using var stream = File.OpenRead(absolutePath);
            var client = new TelegramBotClient(token);
            await client.SendVideo(
                chatId,
                InputFile.FromStream(stream, Path.GetFileName(absolutePath)),
                caption: caption,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return TelegramSendResult.Ok();
        }
        catch (Exception exception) when (exception is ApiRequestException or RequestException or IOException)
        {
            _logger.LogWarning(exception, "Не удалось отправить видео в Telegram");
            return TelegramSendResult.Fail(exception.Message);
        }
    }

    public async Task<TelegramSendResult> SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!TryGetCredentials(out var token, out var chatId))
        {
            return TelegramSendResult.Fail("Telegram не настроен: укажите токен и chat id.");
        }

        try
        {
            var client = new TelegramBotClient(token);
            await client.SendMessage(chatId, text, cancellationToken: cancellationToken).ConfigureAwait(false);
            return TelegramSendResult.Ok();
        }
        catch (Exception exception) when (exception is ApiRequestException or RequestException)
        {
            _logger.LogWarning(exception, "Не удалось отправить сообщение в Telegram");
            return TelegramSendResult.Fail(exception.Message);
        }
    }

    private bool TryGetCredentials(out string token, out long chatId)
    {
        var telegram = _settings.Current.Telegram;
        token = telegram.BotToken?.Trim() ?? string.Empty;
        chatId = 0;

        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return long.TryParse(telegram.ChatId?.Trim(), out chatId);
    }
}
