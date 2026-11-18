using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

/// <summary>Детектор птиц (ONNX-модель YOLO).</summary>
public interface IDetector
{
    bool IsAvailable { get; }

    string Status { get; }

    int InputSize { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DetectedBird>> DetectAsync(VideoFrame frame, CancellationToken cancellationToken = default);
}
