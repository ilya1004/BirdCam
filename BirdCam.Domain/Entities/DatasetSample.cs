using BirdCam.Domain.Enums;

namespace BirdCam.Domain.Entities;

/// <summary>
/// Один пример датасета: полный кадр, YOLO-разметка и вырезка с птицей.
/// Пути хранятся относительно корня хранилища.
/// </summary>
public sealed class DatasetSample
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string ImagePath { get; set; } = string.Empty;

    public string LabelPath { get; set; } = string.Empty;

    public string? CropPath { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public int BirdCount { get; set; }

    public double MaxConfidence { get; set; }

    public DatasetSource Source { get; set; } = DatasetSource.Auto;

    public string? Species { get; set; }

    public bool Reviewed { get; set; }
}
