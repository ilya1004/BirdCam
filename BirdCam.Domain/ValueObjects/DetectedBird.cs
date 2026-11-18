namespace BirdCam.Domain.ValueObjects;

/// <summary>
/// Результат детекции одной птицы на кадре.
/// </summary>
public sealed record DetectedBird(string Label, double Confidence, BoundingBox Box);
