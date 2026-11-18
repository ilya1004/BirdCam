namespace BirdCam.Domain.Entities;

/// <summary>
/// Зафиксированная детекция птицы (нормализованные координаты).
/// </summary>
public sealed class DetectionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? MediaItemId { get; set; }

    public Guid? DatasetSampleId { get; set; }

    public string Label { get; set; } = "bird";

    public double Confidence { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
