using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI.ViewModels;

/// <summary>Страница обучения вида: проверка данных, запуск ultralytics и установка ONNX.</summary>
public sealed partial class TrainingViewModel : ObservableObject
{
    private const int MaxLogLines = 2500;

    private readonly ITrainingService _training;
    private readonly ISettingsProvider _settings;
    private readonly StorageLayout _layout;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ILogger<TrainingViewModel> _logger;
    private readonly Queue<string> _logLines = new();
    private readonly Task _initialRefresh;

    private CancellationTokenSource? _cts;
    private DateTime? _startedAt;
    private string? _lastOnnxPath;
    private string? _lastRunDirectory;
    private bool _refreshing;

    public TrainingViewModel(
        ITrainingService training,
        ISettingsProvider settings,
        StorageLayout layout,
        IDialogService dialogs,
        StatusService status,
        ILogger<TrainingViewModel> logger)
    {
        _training = training;
        _settings = settings;
        _layout = layout;
        _dialogs = dialogs;
        _status = status;
        _logger = logger;

        var trainingSettings = settings.Current.Training ?? new TrainingSettings();
        ClassNames = trainingSettings.ClassNames;
        ValidationPercent = trainingSettings.ValidationPercent;
        Epochs = trainingSettings.Epochs;
        ImageSize = trainingSettings.ImageSize;
        Batch = trainingSettings.Batch;
        Workers = trainingSettings.Workers;
        Device = trainingSettings.Device;
        PythonPath = trainingSettings.PythonPath;

        TrainingDirectory = layout.Training;
        DatasetDirectory = layout.Dataset;
        UpdateActiveModelText();
        _initialRefresh = RefreshAsync();
    }

    /// <summary>Ждёт первичную проверку датасета и окружения (для скриншотов и тестов).</summary>
    public Task EnsureLoadedAsync() => _initialRefresh;

    public string TrainingDirectory { get; }

    public string DatasetDirectory { get; }

    public ObservableCollection<TrainingModelOption> AvailableModels { get; } = [];

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private int _labelCount;

    [ObservableProperty]
    private int _boxCount;

    [ObservableProperty]
    private int _cropCount;

    [ObservableProperty]
    private string _datasetSummary = "Проверка датасета…";

    [ObservableProperty]
    private string _datasetDetails = string.Empty;

    [ObservableProperty]
    private string _dateRangeText = "—";

    [ObservableProperty]
    private string _classesSummary = "—";

    [ObservableProperty]
    private string _environmentSummary = "Проверка Python…";

    [ObservableProperty]
    private bool _isEnvironmentReady;

    [ObservableProperty]
    private string _pythonPath = string.Empty;

    [ObservableProperty]
    private string _pythonVersion = "—";

    [ObservableProperty]
    private string _torchVersion = "—";

    [ObservableProperty]
    private string _ultralyticsVersion = "—";

    [ObservableProperty]
    private string _environmentDetail = string.Empty;

    [ObservableProperty]
    private string _classNames = "синица";

    [ObservableProperty]
    private int _validationPercent = 20;

    [ObservableProperty]
    private TrainingModelOption? _selectedModel;

    [ObservableProperty]
    private int _epochs = 40;

    [ObservableProperty]
    private int _imageSize = 640;

    [ObservableProperty]
    private int _batch = 8;

    [ObservableProperty]
    private int _workers = 2;

    [ObservableProperty]
    private string _device = "cpu";

    [ObservableProperty]
    private string _splitSummary = "Сплит не подготовлен";

    [ObservableProperty]
    private string _statusText = "Проверка окружения…";

    [ObservableProperty]
    private string _progressText = "0 / 0 эпох";

    [ObservableProperty]
    private string _metricsText = "—";

    [ObservableProperty]
    private string _etaText = string.Empty;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isTraining;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _activeModelText = string.Empty;

    public bool HasLog => _logLines.Count > 0;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_refreshing || IsTraining || IsBusy)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var audit = await _training.AuditAsync();
            ApplyAudit(audit);
            await CheckEnvironmentAsync(appendLog: false);
            RefreshAvailableModels();
            RefreshSplitSummary();
            UpdateActiveModelText();
            StatusText = IsEnvironmentReady
                ? "Готово: можно проверить датасет и запускать обучение."
                : "Python с ultralytics не найден — обучение недоступно.";
            AppendLog($"Датасет: {audit.ImageCount} кадров, {audit.BoxCount} рамок, классов {audit.ClassCounts.Count}.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось проверить датасет");
            _status.Error("Обучение: " + exception.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task CheckEnvironmentAsync()
    {
        await CheckEnvironmentAsync(appendLog: true);
    }

    [RelayCommand]
    private async Task PrepareSplitAsync()
    {
        if (IsBusy || IsTraining)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await SaveOptionsAsync();
            var split = await PrepareSplitCoreAsync();
            _status.Set("Сплит датасета готов: " + split.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось подготовить сплит");
            StatusText = exception.Message;
            _status.Error("Обучение: " + exception.Message);
            AppendLog("Ошибка: " + exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartTraining))]
    private async Task StartTrainingAsync()
    {
        if (IsTraining)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await SaveOptionsAsync();
            var split = await PrepareSplitCoreAsync();
            var environment = await _training.DetectEnvironmentAsync();
            ApplyEnvironment(environment);
            if (!environment.IsReady)
            {
                StatusText = environment.Message;
                _status.Error("Обучение: " + environment.Message);
                return;
            }

            var classes = ParseClassNames();
            if (classes.Count == 0)
            {
                throw new InvalidOperationException("Укажите название класса, например «синица».");
            }

            var modelPath = SelectedModel?.Path ?? "yolo26n.pt";
            var runName = $"{TrainingSlug.From(classes[0])}-{DateTime.Now:yyyyMMdd-HHmmss}";
            var options = new TrainingRunOptions(
                split.DataYamlPath,
                modelPath,
                runName,
                Math.Clamp(Epochs, 1, 500),
                Math.Clamp(ImageSize, 320, 1920),
                Math.Clamp(Batch, 1, 64),
                string.IsNullOrWhiteSpace(Device) ? "cpu" : Device.Trim(),
                Math.Clamp(Workers, 0, 16),
                ExportOnnx: true);

            _startedAt = DateTime.Now;
            _lastOnnxPath = null;
            _lastRunDirectory = null;
            HasResult = false;
            ResultText = string.Empty;
            ProgressValue = 0;
            ProgressText = $"0 / {options.Epochs} эпох";
            MetricsText = "—";
            EtaText = string.Empty;
            ClearLog();
            AppendLog($"Запуск: {runName}, {modelPath}, {options.Epochs} эпох, {options.ImageSize}px, batch {options.Batch}.");
            AppendLog($"Python: {environment.PythonPath}");

            _cts = new CancellationTokenSource();
            IsTraining = true;
            StatusText = "Обучение запущено…";

            var progress = new Progress<TrainingProgress>(OnTrainingProgress);
            var result = await _training.TrainAsync(options, progress, _cts.Token);

            if (result.Success)
            {
                _lastOnnxPath = result.BestOnnxPath;
                _lastRunDirectory = result.RunDirectory;
                HasResult = true;
                ResultText = BuildResultText(result);
                StatusText = result.BestOnnxPath is null
                    ? "Обучение завершено, но ONNX не получен. Проверьте журнал."
                    : "Обучение завершено. Модель можно установить и использовать.";
                _status.Set(result.BestOnnxPath is null
                    ? "Обучение завершено без ONNX"
                    : "Обучение завершено: " + Path.GetFileName(result.BestOnnxPath));
            }
            else if (result.Canceled)
            {
                StatusText = "Обучение остановлено.";
                _status.Set("Обучение остановлено");
            }
            else
            {
                StatusText = "Ошибка обучения: " + (result.Error ?? "неизвестная ошибка");
                HasResult = false;
                _status.Error("Обучение: " + result.Error);
                AppendLog("ОШИБКА: " + result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Обучение остановлено.";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка обучения");
            StatusText = exception.Message;
            _status.Error("Обучение: " + exception.Message);
            AppendLog("Ошибка: " + exception.Message);
        }
        finally
        {
            IsTraining = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            StartTrainingCommand.NotifyCanExecuteChanged();
            StopTrainingCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopTraining))]
    private void StopTraining()
    {
        try
        {
            _cts?.Cancel();
            StatusText = "Останавливаю обучение…";
            AppendLog("Запрошена остановка обучения.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось остановить обучение");
        }
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var path = _dialogs.PickFile(
            "ONNX-модель (*.onnx)|*.onnx|Все файлы|*.*",
            _training.TrainingDirectory);

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        _lastOnnxPath = path;
        _lastRunDirectory = Path.GetDirectoryName(path);
        await InstallModelAsync();
    }

    [RelayCommand]
    private async Task InstallModelAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastOnnxPath) || !File.Exists(_lastOnnxPath))
        {
            _status.Error("Нет готового ONNX: сначала обучите модель.");
            return;
        }

        var className = ParseClassNames().FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "синица";
        if (!_dialogs.Confirm(
                $"Установить {Path.GetFileName(_lastOnnxPath)} и использовать её для детекции?\n\n" +
                $"Класс птицы будет переключён на 0, модель — на новый файл."))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _training.InstallAsync(_lastOnnxPath, className);
            UpdateActiveModelText();
            HasResult = true;
            ResultText = string.IsNullOrWhiteSpace(ResultText)
                ? $"Установлено: {result.ModelFileName}\n{result.ModelPath}"
                : ResultText + $"\n\nУстановлено: {result.ModelFileName}";
            StatusText = $"Модель установлена: {result.ModelFileName}. Класс птицы = 0.";
            _status.Set("Модель вида установлена: " + result.ModelFileName);
            AppendLog($"Модель установлена: {result.ModelPath}");
            _dialogs.Info(
                "Модель установлена и активна.\n\n" +
                "Проверьте, что в «Настройках» класс птицы = 0, а модель — " + result.ModelFileName + ".");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось установить модель");
            _status.Error("Установка модели: " + exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void OpenTrainingFolder() => _dialogs.OpenFolder(_training.TrainingDirectory);

    [RelayCommand]
    private void OpenDatasetFolder() => _dialogs.OpenFolder(_layout.Dataset);

    [RelayCommand]
    private void OpenRunFolder()
    {
        var path = _lastRunDirectory ?? _training.TrainingDirectory;
        _dialogs.OpenFolder(path);
    }

    [RelayCommand]
    private void PickPython()
    {
        var path = _dialogs.PickFile("python.exe|python.exe|Все файлы|*.*", PythonPath);
        if (!string.IsNullOrWhiteSpace(path))
        {
            PythonPath = path;
            _ = CheckEnvironmentAsync(appendLog: true);
        }
    }

    [RelayCommand]
    private void CopyTrainingCommand()
    {
        try
        {
            var classes = ParseClassNames();
            var className = classes.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "синица";
            var model = SelectedModel?.Path ?? "yolo26n.pt";
            var python = string.IsNullOrWhiteSpace(PythonPath) ? "python" : PythonPath;
            var yolo = Path.Combine(Path.GetDirectoryName(python) ?? string.Empty, "yolo.exe");
            if (!File.Exists(yolo))
            {
                yolo = "yolo";
            }

            var runName = $"{TrainingSlug.From(className)}-{DateTime.Now:yyyyMMdd-HHmmss}";
            var command = string.Join(
                Environment.NewLine,
                $"cd \"{_layout.Dataset}\"",
                $"& \"{yolo}\" detect train `",
                $"  data=\"{Path.Combine(_layout.Dataset, "data.yaml")}\" `",
                $"  model=\"{model}\" `",
                $"  epochs={Epochs} imgsz={ImageSize} batch={Batch} device={Device} workers={Workers} `",
                $"  project=\"{_layout.TrainingRuns}\" name={runName}");

            Clipboard.SetText(command);
            _status.Set("Команда обучения скопирована в буфер обмена");
            AppendLog("Команда обучения скопирована в буфер обмена.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось скопировать команду");
            _status.Error("Буфер обмена: " + exception.Message);
        }
    }

    [RelayCommand]
    private void ShowPythonHelp()
    {
        var venv = Path.Combine(_training.TrainingDirectory, "venv");
        var python = Path.Combine(venv, "Scripts", "python.exe");
        _dialogs.Info(
            "Для обучения нужен Python 3.12/3.13 с пакетами ultralytics, onnx, onnxslim и onnxruntime.\n\n" +
            "В PowerShell:\n" +
            "py -3.13 -m venv \"" + venv + "\"\n" +
            "\"" + python + "\" -m pip install --upgrade pip\n" +
            "\"" + python + "\" -m pip install ultralytics onnx onnxslim onnxruntime\n\n" +
            "Затем укажите путь к python.exe в поле «Python» и нажмите «Проверить».\n\n" +
            "Можно использовать существующее окружение проекта: .tools\\venv\\Scripts\\python.exe.");
    }

    private async Task CheckEnvironmentAsync(bool appendLog)
    {
        try
        {
            var environment = await _training.DetectEnvironmentAsync();
            ApplyEnvironment(environment);
            if (appendLog)
            {
                AppendLog("Окружение: " + environment.Message);
            }

            if (environment.IsReady && string.IsNullOrWhiteSpace(PythonPath))
            {
                PythonPath = environment.PythonPath;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Не удалось проверить окружение");
            EnvironmentSummary = "Ошибка проверки: " + exception.Message;
            IsEnvironmentReady = false;
        }
    }

    private async Task<TrainingSplitResult> PrepareSplitCoreAsync()
    {
        var classes = ParseClassNames();
        if (classes.Count == 0)
        {
            throw new InvalidOperationException("Укажите название класса, например «синица».");
        }

        StatusText = "Подготовка train/val и data.yaml…";
        var split = await _training.PrepareSplitAsync(
            new TrainingSplitOptions(classes, Math.Clamp(ValidationPercent, 5, 50)));

        SplitSummary = split.Message;
        AppendLog("Сплит: " + split.Message);
        AppendLog($"data.yaml: {split.DataYamlPath}");
        return split;
    }

    private void RefreshSplitSummary()
    {
        var trainPath = Path.Combine(_layout.Dataset, "train.txt");
        var valPath = Path.Combine(_layout.Dataset, "val.txt");
        var yamlPath = Path.Combine(_layout.Dataset, "data.yaml");

        if (!File.Exists(trainPath) || !File.Exists(valPath) || !File.Exists(yamlPath))
        {
            SplitSummary = "Сплит не подготовлен";
            return;
        }

        try
        {
            var train = File.ReadLines(trainPath).Count(line => line.Trim().Length > 0);
            var val = File.ReadLines(valPath).Count(line => line.Trim().Length > 0);
            SplitSummary = $"train {train}, val {val}: файлы готовы (последние кадры — валидация)";
        }
        catch (IOException)
        {
            SplitSummary = "Сплит не подготовлен";
        }
    }

    private async Task SaveOptionsAsync()
    {
        var current = _settings.Current.Clone();
        current.Training ??= new TrainingSettings();
        current.Training.PythonPath = PythonPath?.Trim() ?? string.Empty;
        current.Training.ClassNames = ClassNames?.Trim() ?? "синица";
        current.Training.ValidationPercent = Math.Clamp(ValidationPercent, 5, 50);
        current.Training.BaseModel = SelectedModel?.Name ?? "yolo26n.pt";
        current.Training.Epochs = Math.Clamp(Epochs, 1, 500);
        current.Training.ImageSize = Math.Clamp(ImageSize, 320, 1920);
        current.Training.Batch = Math.Clamp(Batch, 1, 64);
        current.Training.Workers = Math.Clamp(Workers, 0, 16);
        current.Training.Device = string.IsNullOrWhiteSpace(Device) ? "cpu" : Device.Trim();
        await _settings.SaveAsync(current);
    }

    private void ApplyAudit(TrainingAudit audit)
    {
        ImageCount = audit.ImageCount;
        LabelCount = audit.LabelCount;
        BoxCount = audit.BoxCount;
        CropCount = audit.CropCount;
        DatasetSummary = $"{audit.ImageCount} кадров · {audit.BoxCount} рамок · {audit.CropCount} кропов";
        DateRangeText = audit.FirstImageAt is null || audit.LastImageAt is null
            ? "время кадров не определено"
            : $"{audit.FirstImageAt:dd.MM.yyyy HH:mm} — {audit.LastImageAt:dd.MM.yyyy HH:mm}";

        DatasetDetails = audit.Warnings.Count == 0
            ? $"Разметка: {audit.LabelCount} файлов, ошибок нет."
            : string.Join(Environment.NewLine, audit.Warnings);

        ClassesSummary = BuildClassesSummary(audit);
    }

    private string BuildClassesSummary(TrainingAudit audit)
    {
        if (audit.ClassCounts.Count == 0)
        {
            return "классы не найдены";
        }

        var names = ParseClassNames();
        return string.Join(
            " · ",
            audit.ClassCounts
                .OrderBy(pair => pair.Key)
                .Select(pair =>
                {
                    var name = pair.Key < names.Count && !string.IsNullOrWhiteSpace(names[pair.Key])
                        ? names[pair.Key]
                        : $"класс {pair.Key}";
                    return $"{name}: {pair.Value}";
                }));
    }

    private void ApplyEnvironment(TrainingEnvironment environment)
    {
        IsEnvironmentReady = environment.IsReady;
        EnvironmentSummary = environment.Message;
        PythonVersion = environment.PythonVersion;
        TorchVersion = environment.TorchVersion;
        UltralyticsVersion = environment.UltralyticsVersion;

        if (environment.CudaAvailable)
        {
            var gpuName = string.IsNullOrWhiteSpace(environment.GpuName) ? "GPU" : environment.GpuName;
            EnvironmentDetail = $"GPU: {gpuName} · torch {environment.TorchVersion} · потоков CPU: {environment.CpuThreads}";
            if (string.Equals(Device, "cpu", StringComparison.OrdinalIgnoreCase))
            {
                Device = "0";
                AppendLog("Обнаружена GPU: устройство обучения переключено на 0.");
            }
        }
        else
        {
            EnvironmentDetail = $"Потоков CPU: {environment.CpuThreads} · обучение на CPU (GPU не найдена)";
            if (string.Equals(Device, "0", StringComparison.OrdinalIgnoreCase))
            {
                Device = "cpu";
                AppendLog("GPU не найдена: устройство обучения переключено на cpu.");
            }
        }

        if (!string.IsNullOrWhiteSpace(environment.PythonPath))
        {
            PythonPath = environment.PythonPath;
        }

        StartTrainingCommand.NotifyCanExecuteChanged();
    }

    private void OnTrainingProgress(TrainingProgress progress)
    {
        if (progress.IsLog)
        {
            AppendLog(progress.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            StatusText = progress.Message;
            AppendLog(progress.Message);
        }

        if (progress.Epochs > 0)
        {
            ProgressText = $"{Math.Min(progress.Epoch, progress.Epochs)} / {progress.Epochs} эпох";
        }

        if (progress.Percent > 0)
        {
            ProgressValue = Math.Clamp(progress.Percent, 0, 100);
        }

        if (progress.Metrics is { Count: > 0 })
        {
            MetricsText = FormatMetrics(progress.Metrics);
        }

        if (progress.Phase is TrainingPhase.Exporting)
        {
            ProgressValue = 100;
        }

        if (progress.Phase is TrainingPhase.Completed && _startedAt is not null)
        {
            EtaText = "завершено за " + FormatDuration(DateTime.Now - _startedAt.Value);
        }
        else
        {
            UpdateEta(progress);
        }
    }

    private void UpdateEta(TrainingProgress progress)
    {
        if (_startedAt is null || progress.Epoch <= 0)
        {
            return;
        }

        var elapsed = DateTime.Now - _startedAt.Value;
        if (elapsed.TotalSeconds < 5)
        {
            return;
        }

        var perEpoch = elapsed.TotalSeconds / progress.Epoch;
        var remaining = TimeSpan.FromSeconds(Math.Max(0, perEpoch * (progress.Epochs - progress.Epoch)));
        EtaText = remaining.TotalMinutes >= 1
            ? $"прошло {FormatDuration(elapsed)} · осталось ~{FormatDuration(remaining)}"
            : $"прошло {FormatDuration(elapsed)}";
    }

    private static string FormatMetrics(IReadOnlyDictionary<string, double> metrics)
    {
        var map50 = Find(metrics, "metrics/mAP50(B)");
        var map5095 = Find(metrics, "metrics/mAP50-95(B)");
        var precision = Find(metrics, "metrics/precision(B)");
        var recall = Find(metrics, "metrics/recall(B)");

        var builder = new StringBuilder();
        if (map50 is not null)
        {
            builder.Append($"mAP50 {map50.Value.ToString("P1", CultureInfo.CurrentCulture)}");
        }

        if (map5095 is not null)
        {
            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append($"mAP50-95 {map5095.Value.ToString("P1", CultureInfo.CurrentCulture)}");
        }

        if (precision is not null)
        {
            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append($"P {precision.Value.ToString("P0", CultureInfo.CurrentCulture)}");
        }

        if (recall is not null)
        {
            if (builder.Length > 0)
            {
                builder.Append(" · ");
            }

            builder.Append($"R {recall.Value.ToString("P0", CultureInfo.CurrentCulture)}");
        }

        return builder.Length == 0 ? "—" : builder.ToString();
    }

    private static double? Find(IReadOnlyDictionary<string, double> metrics, string key)
    {
        if (metrics.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var pair in metrics)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string FormatDuration(TimeSpan time)
    {
        if (time.TotalHours >= 1)
        {
            return $"{(int)time.TotalHours} ч {time.Minutes} мин";
        }

        return time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} мин" : $"{time.TotalSeconds:0} с";
    }

    private string BuildResultText(TrainingResult result)
    {
        if (result.BestOnnxPath is null)
        {
            return "Обучение завершено, но ONNX не получен.\n" +
                   "best.pt: " + (result.BestWeightsPath ?? "—") + "\n" +
                   "Журнал: " + (result.LogPath ?? "—");
        }

        var file = new FileInfo(result.BestOnnxPath);
        var builder = new StringBuilder();
        builder.AppendLine($"ONNX: {result.BestOnnxPath}");
        builder.AppendLine($"Размер: {file.Length / 1024d / 1024d:0.0} МБ");
        if (result.Metrics is { Count: > 0 })
        {
            builder.AppendLine(FormatMetrics(result.Metrics));
        }

        builder.AppendLine($"Запуск: {result.RunDirectory}");
        builder.Append($"Журнал: {result.LogPath}");
        return builder.ToString();
    }

    private List<string> ParseClassNames()
    {
        var lines = (ClassNames ?? string.Empty)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private void RefreshAvailableModels()
    {
        var candidates = new List<TrainingModelOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in EnumerateModelRoots())
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var path in Directory.EnumerateFiles(root, "*.pt", SearchOption.AllDirectories))
                {
                    if (!seen.Add(Path.GetFullPath(path)))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    candidates.Add(new TrainingModelOption(
                        Path.GetFileName(path),
                        Path.GetFullPath(path),
                        $"{info.Length / 1024d / 1024d:0.0} МБ"));
                }
            }
            catch (IOException exception)
            {
                _logger.LogDebug(exception, "Не удалось прочитать папку моделей {Root}", root);
            }
        }

        if (candidates.Count == 0)
        {
            candidates.Add(new TrainingModelOption("yolo26n.pt", "yolo26n.pt", "скачается автоматически"));
        }

        var ordered = candidates
            .GroupBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AvailableModels.Clear();
        foreach (var model in ordered)
        {
            AvailableModels.Add(model);
        }

        var configured = _settings.Current.Training?.BaseModel ?? "yolo26n.pt";
        SelectedModel =
            AvailableModels.FirstOrDefault(model =>
                string.Equals(model.Path, configured, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Name, Path.GetFileName(configured), StringComparison.OrdinalIgnoreCase)) ??
            AvailableModels.FirstOrDefault(model => model.Name.StartsWith("yolo26n", StringComparison.OrdinalIgnoreCase)) ??
            AvailableModels.FirstOrDefault();
    }

    private IEnumerable<string> EnumerateModelRoots()
    {
        yield return _layout.Models;
        yield return Path.Combine(AppContext.BaseDirectory, "models");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            yield return Path.Combine(directory.FullName, "models");
        }
    }

    private void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        foreach (var line in message.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            _logLines.Enqueue($"{DateTime.Now:HH:mm:ss} {line.TrimEnd()}");
            while (_logLines.Count > MaxLogLines)
            {
                _logLines.Dequeue();
            }
        }

        LogText = string.Join(Environment.NewLine, _logLines);
    }

    private void ClearLog()
    {
        _logLines.Clear();
        LogText = string.Empty;
    }

    private void UpdateActiveModelText()
    {
        var detection = _settings.Current.Detection ?? new DetectionSettings();
        ActiveModelText = $"Сейчас используется: {detection.ModelPath} · классы птиц {detection.BirdClassIds}";
    }

    private bool CanStartTraining() => !IsTraining && !IsBusy && IsEnvironmentReady;

    private bool CanStopTraining() => IsTraining;

    partial void OnIsTrainingChanged(bool value)
    {
        StartTrainingCommand.NotifyCanExecuteChanged();
        StopTrainingCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value) => StartTrainingCommand.NotifyCanExecuteChanged();

    partial void OnIsEnvironmentReadyChanged(bool value) => StartTrainingCommand.NotifyCanExecuteChanged();
}

/// <summary>Пункт списка .pt-моделей для дообучения.</summary>
public sealed record TrainingModelOption(string Name, string Path, string Details)
{
    public string DisplayName => $"{Name} — {Details}";

    public override string ToString() => DisplayName;
}
