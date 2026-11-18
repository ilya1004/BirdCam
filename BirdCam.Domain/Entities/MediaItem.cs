using BirdCam.Domain.Enums;

namespace BirdCam.Domain.Entities;

/// <summary>
/// Снимок или видеоклип, сохранённый приложением.
/// </summary>
public sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MediaKind Kind { get; set; }

    /// <summary>Путь относительно корня хранилища.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Путь превью относительно корня хранилища.</summary>
    public string? ThumbnailPath { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int Width { get; set; }

    public int Height { get; set; }

    public double DurationSeconds { get; set; }

    public long SizeBytes { get; set; }

    public DateTimeOffset? SentToTelegramAtUtc { get; set; }

    public string? Note { get; set; }
}
