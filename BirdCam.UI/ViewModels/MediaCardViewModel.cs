using BirdCam.Application.Abstractions;
using BirdCam.Domain.Entities;
using BirdCam.Domain.Enums;

namespace BirdCam.UI.ViewModels;

public sealed class MediaCardViewModel
{
    private MediaCardViewModel(
        Guid id,
        string title,
        string subtitle,
        string absolutePath,
        string? previewPath,
        bool isClip,
        bool sentToTelegram)
    {
        Id = id;
        Title = title;
        Subtitle = subtitle;
        AbsolutePath = absolutePath;
        PreviewPath = previewPath;
        IsClip = isClip;
        SentToTelegram = sentToTelegram;
    }

    public Guid Id { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public string AbsolutePath { get; }

    public string? PreviewPath { get; }

    public bool IsClip { get; }

    public bool SentToTelegram { get; }

    public string Badge => IsClip ? "видео" : "фото";

    public static MediaCardViewModel From(MediaItem item, IFileStorage storage)
    {
        var local = item.CreatedAtUtc.ToLocalTime();
        var isClip = item.Kind == MediaKind.Clip;

        var title = isClip
            ? $"Клип · {local:HH:mm:ss}"
            : $"Снимок · {local:HH:mm:ss}";

        var size = item.SizeBytes > 0 ? $" · {item.SizeBytes / 1024d / 1024d:0.0} МБ" : string.Empty;
        var duration = isClip && item.DurationSeconds > 0 ? $" · {item.DurationSeconds:0.#} с" : string.Empty;
        var subtitle = $"{local:dd.MM.yyyy}{duration}{size}";

        var preview = item.ThumbnailPath is not null
            ? storage.GetAbsolutePath(item.ThumbnailPath)
            : isClip ? null : storage.GetAbsolutePath(item.FilePath);

        return new MediaCardViewModel(
            item.Id,
            title,
            subtitle,
            storage.GetAbsolutePath(item.FilePath),
            preview,
            isClip,
            item.SentToTelegramAtUtc is not null);
    }
}
