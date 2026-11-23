using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using Microsoft.Extensions.Logging;

namespace BirdCam.Application.Services;

/// <summary>
/// Оркестрация обучения: проверка датасета, подготовка сплита, запуск Python-процесса
/// и установка полученного ONNX в приложение.
/// </summary>
public sealed class TrainingService : ITrainingService
{
    private readonly ITrainingDataService _data;
    private readonly ITrainingRunner _runner;
    private readonly ISettingsProvider _settings;
    private readonly IDetector _detector;
    private readonly StorageLayout _layout;
    private readonly ILogger<TrainingService> _logger;

    public TrainingService(
        ITrainingDataService data,
        ITrainingRunner runner,
        ISettingsProvider settings,
        IDetector detector,
        StorageLayout layout,
        ILogger<TrainingService> logger)
    {
        _data = data;
        _runner = runner;
        _settings = settings;
        _detector = detector;
        _layout = layout;
        _logger = logger;
    }

    public string TrainingDirectory => _layout.Training;

    public Task<TrainingAudit> AuditAsync(CancellationToken cancellationToken = default)
        => _data.AuditAsync(cancellationToken);

    public Task<TrainingSplitResult> PrepareSplitAsync(
        TrainingSplitOptions options,
        CancellationToken cancellationToken = default)
        => _data.PrepareSplitAsync(options, cancellationToken);

    public Task<TrainingEnvironment> DetectEnvironmentAsync(CancellationToken cancellationToken = default)
        => _runner.DetectEnvironmentAsync(cancellationToken);

    public Task<TrainingResult> TrainAsync(
        TrainingRunOptions options,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _runner.TrainAsync(options, progress, cancellationToken);

    public async Task<ModelInstallResult> InstallAsync(
        string onnxPath,
        string className,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onnxPath);
        if (!File.Exists(onnxPath))
        {
            throw new FileNotFoundException("ONNX-модель не найдена.", onnxPath);
        }

        var slug = TrainingSlug.From(className);
        var fileName = $"{slug}-{DateTime.Now:yyyyMMdd-HHmmss}.onnx";
        var installedPath = await _runner
            .InstallModelAsync(onnxPath, fileName, cancellationToken)
            .ConfigureAwait(false);

        var current = _settings.Current.Clone();
        current.Detection ??= new DetectionSettings();
        current.Detection.ModelPath = Path.GetFileName(installedPath);
        current.Detection.BirdClassIds = "0";

        await _settings.SaveAsync(current, cancellationToken).ConfigureAwait(false);
        await _detector.InitializeAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Модель вида установлена: {Path}, класс «{Class}», статус детектора: {Status}",
            installedPath,
            className,
            _detector.Status);

        return new ModelInstallResult(Path.GetFileName(installedPath), installedPath, className);
    }
}
