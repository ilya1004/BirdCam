using BirdCam.Application.Training;

namespace BirdCam.Application.Abstractions;

/// <summary>Проверка датасета и подготовка train/val-сплита для ultralytics.</summary>
public interface ITrainingDataService
{
    Task<TrainingAudit> AuditAsync(CancellationToken cancellationToken = default);

    Task<TrainingSplitResult> PrepareSplitAsync(
        TrainingSplitOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>Запуск обучения в отдельном Python-процессе и установка готовой ONNX-модели.</summary>
public interface ITrainingRunner
{
    Task<TrainingEnvironment> DetectEnvironmentAsync(CancellationToken cancellationToken = default);

    Task<TrainingResult> TrainAsync(
        TrainingRunOptions options,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<string> InstallModelAsync(
        string onnxPath,
        string fileName,
        CancellationToken cancellationToken = default);
}

/// <summary>Сценарий обучения вида: проверить данные, обучить, установить модель.</summary>
public interface ITrainingService
{
    string TrainingDirectory { get; }

    Task<TrainingAudit> AuditAsync(CancellationToken cancellationToken = default);

    Task<TrainingSplitResult> PrepareSplitAsync(
        TrainingSplitOptions options,
        CancellationToken cancellationToken = default);

    Task<TrainingEnvironment> DetectEnvironmentAsync(CancellationToken cancellationToken = default);

    Task<TrainingResult> TrainAsync(
        TrainingRunOptions options,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<ModelInstallResult> InstallAsync(
        string onnxPath,
        string className,
        CancellationToken cancellationToken = default);
}
