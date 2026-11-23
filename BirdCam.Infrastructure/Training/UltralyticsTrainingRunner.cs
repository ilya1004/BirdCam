using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using Microsoft.Extensions.Logging;

namespace BirdCam.Infrastructure.Training;

/// <summary>
/// Запускает обучение ultralytics отдельным процессом Python, транслирует
/// прогресс в приложение и устанавливает best.onnx в папку моделей.
/// </summary>
public sealed partial class UltralyticsTrainingRunner : ITrainingRunner, IDisposable
{
    private const string MarkerPrefix = "BIRDCAM:";

    private readonly ISettingsProvider _settings;
    private readonly StorageLayout _layout;
    private readonly ILogger<UltralyticsTrainingRunner> _logger;
    private readonly object _gate = new();

    private Process? _process;
    private bool _disposed;

    public UltralyticsTrainingRunner(
        ISettingsProvider settings,
        StorageLayout layout,
        ILogger<UltralyticsTrainingRunner> logger)
    {
        _settings = settings;
        _layout = layout;
        _logger = logger;
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[a-zA-Z]")]
    private static partial Regex AnsiRegex();

    public async Task<TrainingEnvironment> DetectEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        var candidates = EnumeratePythonCandidates().ToList();
        if (candidates.Count == 0)
        {
            return new TrainingEnvironment(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                false,
                Environment.ProcessorCount,
                "Python не найден. Установите Python 3.12/3.13 и выполните " +
                "python -m pip install ultralytics onnx onnxslim onnxruntime");
        }

        TrainingEnvironment? partial = null;
        var errors = new List<string>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var probe = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (probe is null)
            {
                continue;
            }

            var environment = probe.ToEnvironment();
            if (environment.IsReady)
            {
                return environment;
            }

            partial ??= environment;
            if (!string.IsNullOrWhiteSpace(probe.Error))
            {
                errors.Add($"{candidate.Display}: {probe.Error}");
            }
        }

        if (partial is not null)
        {
            return partial with
            {
                Message = partial.Message +
                          (errors.Count > 0 ? " Подробности: " + string.Join("; ", errors.Take(3)) : string.Empty)
            };
        }

        return new TrainingEnvironment(
            false,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            Environment.ProcessorCount,
            "Python-интерпретаторы найдены, но ни один не запустился. Проверьте путь в настройках обучения.");
    }

    public async Task<TrainingResult> TrainAsync(
        TrainingRunOptions options,
        IProgress<TrainingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        progress?.Report(new TrainingProgress(TrainingPhase.Setup, "Проверка Python и ultralytics…"));

        var environment = await DetectEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        if (!environment.IsReady)
        {
            return TrainingResult.Fail(environment.Message);
        }

        if (!File.Exists(options.DataYamlPath))
        {
            return TrainingResult.Fail("Не найден data.yaml. Сначала подготовьте датасет.");
        }

        var runName = MakeUniqueRunName(options.RunName);
        var runDirectory = Path.Combine(_layout.TrainingRuns, runName);
        var logPath = Path.Combine(_layout.TrainingLogs, runName + ".log");
        Directory.CreateDirectory(_layout.Training);
        Directory.CreateDirectory(_layout.TrainingRuns);
        Directory.CreateDirectory(_layout.TrainingLogs);

        var scriptPath = Path.Combine(_layout.Training, TrainingScript.FileName);
        if (!File.Exists(scriptPath) ||
            !string.Equals(await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false),
                TrainingScript.Content, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(scriptPath, TrainingScript.Content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
        }

        var baseModel = ResolveBaseModel(options.BaseModel);
        var startInfo = CreateStartInfo(environment.PythonPath, scriptPath, options, runName, baseModel);

        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        await log.WriteLineAsync($"BirdCam training {DateTime.Now:yyyy-MM-dd HH:mm:ss}").ConfigureAwait(false);
        await log.WriteLineAsync($"Python: {environment.PythonPath} ({environment.PythonVersion})").ConfigureAwait(false);
        await log.WriteLineAsync($"ultralytics: {environment.UltralyticsVersion}, torch: {environment.TorchVersion}").ConfigureAwait(false);
        await log.WriteLineAsync($"command: {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)}").ConfigureAwait(false);
        await log.WriteLineAsync(new string('-', 80)).ConfigureAwait(false);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        lock (_gate)
        {
            _process = process;
        }

        var state = new TrainingState(options.Epochs);
        try
        {
            if (!process.Start())
            {
                return TrainingResult.Fail("Не удалось запустить Python-процесс.", logPath);
            }

            progress?.Report(new TrainingProgress(
                TrainingPhase.Setup,
                $"Python {environment.PythonVersion}, ultralytics {environment.UltralyticsVersion}, " +
                (environment.CudaAvailable ? "CUDA" : "CPU") + ". Запуск…"));

            var stdout = PumpAsync(process.StandardOutput, state, progress, log, cancellationToken);
            var stderr = PumpAsync(process.StandardError, state, progress, log, cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcess(process);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await log.WriteLineAsync("Отменено пользователем.").ConfigureAwait(false);
                progress?.Report(new TrainingProgress(TrainingPhase.Canceled, "Обучение остановлено."));
                return TrainingResult.Cancelled(logPath);
            }

            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await log.WriteLineAsync($"exit code: {process.ExitCode}").ConfigureAwait(false);
            await log.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (state.Error is not null)
            {
                _logger.LogWarning("Обучение завершилось с ошибкой: {Error}", state.Error);
                progress?.Report(new TrainingProgress(TrainingPhase.Failed, state.Error));
                return TrainingResult.Fail(state.Error, logPath);
            }

            if (process.ExitCode != 0)
            {
                var error = $"Python завершился с кодом {process.ExitCode}. Подробности в журнале.";
                progress?.Report(new TrainingProgress(TrainingPhase.Failed, error));
                return TrainingResult.Fail(error, logPath);
            }

            if (string.IsNullOrWhiteSpace(state.BestWeightsPath) || !File.Exists(state.BestWeightsPath))
            {
                var error = "Обучение завершилось, но best.pt не найден.";
                progress?.Report(new TrainingProgress(TrainingPhase.Failed, error));
                return TrainingResult.Fail(error, logPath);
            }

            if (!string.IsNullOrWhiteSpace(state.BestOnnxPath) && !File.Exists(state.BestOnnxPath))
            {
                state.BestOnnxPath = null;
            }

            var message = state.BestOnnxPath is null
                ? "Обучение завершено, ONNX не получен."
                : $"Готово: {Path.GetFileName(state.BestOnnxPath)}";

            progress?.Report(new TrainingProgress(
                TrainingPhase.Completed,
                message,
                state.Epoch,
                options.Epochs,
                100,
                state.MetricsSnapshot()));

            _logger.LogInformation(
                "Обучение завершено: {Run}, best.pt: {Best}, onnx: {Onnx}",
                runDirectory,
                state.BestWeightsPath,
                state.BestOnnxPath ?? "-");

            return new TrainingResult(
                true,
                false,
                state.RunDirectory ?? runDirectory,
                state.BestWeightsPath,
                state.BestOnnxPath,
                logPath,
                null,
                state.MetricsSnapshot());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Ошибка запуска обучения");
            return TrainingResult.Fail("Ошибка обучения: " + exception.Message, logPath);
        }
        finally
        {
            lock (_gate)
            {
                _process = null;
            }
        }
    }

    public Task<string> InstallModelAsync(
        string onnxPath,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(onnxPath);
        if (!File.Exists(onnxPath))
        {
            throw new FileNotFoundException("ONNX-модель не найдена.", onnxPath);
        }

        Directory.CreateDirectory(_layout.Models);
        var target = Path.Combine(_layout.Models, Path.GetFileName(fileName));
        File.Copy(onnxPath, target, overwrite: true);
        return Task.FromResult(target);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is not null)
        {
            KillProcess(process);
        }
    }

    private async Task PumpAsync(
        StreamReader reader,
        TrainingState state,
        IProgress<TrainingProgress>? progress,
        StreamWriter log,
        CancellationToken cancellationToken)
    {
        // Читаем именно символы, а не строки: tqdm пишет прогресс через \r без \n.
        // ReadLineAsync в этом случае не опустошает pipe, и Python блокируется на записи.
        var buffer = new char[4096];
        var pending = new StringBuilder(256);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var symbol = buffer[index];
                    if (symbol is not ('\r' or '\n'))
                    {
                        pending.Append(symbol);
                        if (pending.Length > 16_384)
                        {
                            ProcessSegment(pending.ToString(), state, progress, log);
                            pending.Clear();
                        }

                        continue;
                    }

                    if (pending.Length == 0)
                    {
                        continue;
                    }

                    var text = pending.ToString();
                    pending.Clear();
                    var isProgress = symbol == '\r' && LooksLikeProgress(text);
                    if (isProgress && !ShouldLogProgress(state))
                    {
                        continue;
                    }

                    ProcessSegment(text, state, progress, log);
                }
            }

            if (pending.Length > 0)
            {
                ProcessSegment(pending.ToString(), state, progress, log);
            }
        }
        catch (OperationCanceledException)
        {
            // процесс остановлен
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Ошибка чтения вывода Python");
        }
    }

    private void ProcessSegment(
        string text,
        TrainingState state,
        IProgress<TrainingProgress>? progress,
        StreamWriter log)
    {
        var cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return;
        }

        // stdout и stderr читаются параллельно: StreamWriter и состояние должны быть под замком.
        lock (state.LogGate)
        {
            log.WriteLine(cleaned);
            HandleLine(cleaned, state, progress);
        }
    }

    private static bool ShouldLogProgress(TrainingState state)
    {
        var now = DateTime.UtcNow;
        lock (state.ProgressGate)
        {
            if (now - state.LastProgressAtUtc < TimeSpan.FromSeconds(2))
            {
                return false;
            }

            state.LastProgressAtUtc = now;
            return true;
        }
    }

    private static bool LooksLikeProgress(string text)
        => text.Contains("it/s", StringComparison.Ordinal) ||
           text.Contains("s/it", StringComparison.Ordinal) ||
           text.Contains("━", StringComparison.Ordinal) ||
           text.Contains("╸", StringComparison.Ordinal);

    private void HandleLine(string line, TrainingState state, IProgress<TrainingProgress>? progress)
    {
        if (line.StartsWith(MarkerPrefix, StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(line[MarkerPrefix.Length..]);
                HandleMarker(document.RootElement, state, progress);
                return;
            }
            catch (JsonException exception)
            {
                _logger.LogDebug(exception, "Некорректная строка прогресса: {Line}", line);
            }
        }

        progress?.Report(new TrainingProgress(
            state.Phase,
            line,
            state.Epoch,
            state.TotalEpochs,
            state.Percent,
            IsLog: true));
    }

    private void HandleMarker(JsonElement root, TrainingState state, IProgress<TrainingProgress>? progress)
    {
        var kind = root.TryGetProperty("kind", out var kindElement) ? kindElement.GetString() : null;
        switch (kind)
        {
            case "start":
            {
                var epochs = GetInt(root, "epochs", state.TotalEpochs);
                state.TotalEpochs = epochs;
                state.Phase = TrainingPhase.Training;
                progress?.Report(new TrainingProgress(
                    TrainingPhase.Training,
                    $"Обучение: {epochs} эпох, {GetString(root, "model")}",
                    0,
                    epochs,
                    0));
                break;
            }
            case "epoch":
            {
                state.Epoch = GetInt(root, "epoch", state.Epoch);
                state.TotalEpochs = GetInt(root, "epochs", state.TotalEpochs);
                state.Percent = state.TotalEpochs > 0 ? state.Epoch * 100d / state.TotalEpochs : 0;
                var loss = GetDouble(root, "loss");
                var text = loss is null
                    ? $"Эпоха {state.Epoch}/{state.TotalEpochs}"
                    : $"Эпоха {state.Epoch}/{state.TotalEpochs}, loss {loss:0.000}";
                progress?.Report(new TrainingProgress(
                    TrainingPhase.Training,
                    text,
                    state.Epoch,
                    state.TotalEpochs,
                    state.Percent,
                    state.MetricsSnapshot()));
                break;
            }
            case "metrics":
            {
                state.Epoch = GetInt(root, "epoch", state.Epoch);
                state.TotalEpochs = GetInt(root, "epochs", state.TotalEpochs);
                state.SetMetrics(root);
                state.Percent = state.TotalEpochs > 0 ? state.Epoch * 100d / state.TotalEpochs : 0;
                progress?.Report(new TrainingProgress(
                    TrainingPhase.Training,
                    $"Эпоха {state.Epoch}/{state.TotalEpochs}: " + state.MetricsText(),
                    state.Epoch,
                    state.TotalEpochs,
                    state.Percent,
                    state.MetricsSnapshot()));
                break;
            }
            case "trained":
            {
                state.BestWeightsPath = GetString(root, "best");
                state.RunDirectory = GetString(root, "run");
                progress?.Report(new TrainingProgress(
                    TrainingPhase.Exporting,
                    "Обучение завершено, экспорт в ONNX…",
                    state.TotalEpochs,
                    state.TotalEpochs,
                    100,
                    state.MetricsSnapshot()));
                break;
            }
            case "phase":
            {
                var phase = GetString(root, "phase");
                var message = GetString(root, "message");
                if (string.Equals(phase, "exporting", StringComparison.OrdinalIgnoreCase))
                {
                    state.Phase = TrainingPhase.Exporting;
                    progress?.Report(new TrainingProgress(
                        TrainingPhase.Exporting,
                        string.IsNullOrWhiteSpace(message) ? "Экспорт в ONNX…" : message,
                        state.TotalEpochs,
                        state.TotalEpochs,
                        100,
                        state.MetricsSnapshot()));
                }

                break;
            }
            case "exported":
            {
                state.BestOnnxPath = GetString(root, "onnx");
                state.BestWeightsPath = GetString(root, "best") ?? state.BestWeightsPath;
                break;
            }
            case "done":
            {
                state.BestWeightsPath = GetString(root, "best") ?? state.BestWeightsPath;
                state.BestOnnxPath = GetString(root, "onnx") ?? state.BestOnnxPath;
                state.RunDirectory = GetString(root, "run") ?? state.RunDirectory;
                break;
            }
            case "error":
            {
                var message = GetString(root, "message") ?? "Неизвестная ошибка обучения.";
                var traceback = GetString(root, "traceback");
                state.Error = traceback is null ? message : message + Environment.NewLine + traceback;
                state.Phase = TrainingPhase.Failed;
                break;
            }
        }
    }

    private ProcessStartInfo CreateStartInfo(
        string pythonPath,
        string scriptPath,
        TrainingRunOptions options,
        string runName,
        string baseModel)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            WorkingDirectory = _layout.Training,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("--data");
        startInfo.ArgumentList.Add(options.DataYamlPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(baseModel);
        startInfo.ArgumentList.Add("--run-name");
        startInfo.ArgumentList.Add(runName);
        startInfo.ArgumentList.Add("--epochs");
        startInfo.ArgumentList.Add(options.Epochs.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--imgsz");
        startInfo.ArgumentList.Add(options.ImageSize.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--batch");
        startInfo.ArgumentList.Add(options.Batch.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--device");
        startInfo.ArgumentList.Add(string.IsNullOrWhiteSpace(options.Device) ? "cpu" : options.Device.Trim());
        startInfo.ArgumentList.Add("--workers");
        startInfo.ArgumentList.Add(Math.Max(0, options.Workers).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(_layout.TrainingRuns);
        startInfo.ArgumentList.Add("--opset");
        startInfo.ArgumentList.Add("18");

        if (!options.ExportOnnx)
        {
            startInfo.ArgumentList.Add("--no-export");
        }

        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["MPLBACKEND"] = "Agg";
        startInfo.Environment["TQDM_DISABLE"] = "1";
        return startInfo;
    }

    private IEnumerable<PythonCandidate> EnumeratePythonCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = _settings.Current.Training?.PythonPath?.Trim();

        foreach (var candidate in RawCandidates(configured))
        {
            var key = candidate.FileName + "|" + string.Join(' ', candidate.PrefixArguments);
            if (seen.Add(key) && File.Exists(candidate.FileName))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<PythonCandidate> RawCandidates(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            yield return new PythonCandidate(configured, [], "настройки");
        }

        yield return new PythonCandidate(
            Path.Combine(_layout.TrainingVenv, "Scripts", "python.exe"), [], "обучение/venv");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            yield return new PythonCandidate(
                Path.Combine(directory.FullName, ".tools", "venv", "Scripts", "python.exe"), [], ".tools/venv");
            yield return new PythonCandidate(
                Path.Combine(directory.FullName, "venv", "Scripts", "python.exe"), [], "venv");
            yield return new PythonCandidate(
                Path.Combine(directory.FullName, ".venv", "Scripts", "python.exe"), [], ".venv");
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return new PythonCandidate(Path.Combine(folder, "python.exe"), [], "PATH");
            yield return new PythonCandidate(Path.Combine(folder, "python3.exe"), [], "PATH");
        }

        var py = FindOnPath("py.exe");
        if (py is not null)
        {
            yield return new PythonCandidate(py, ["-3.13"], "py -3.13");
            yield return new PythonCandidate(py, ["-3.12"], "py -3.12");
        }
    }

    private async Task<PythonProbe?> ProbeAsync(PythonCandidate candidate, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        var startInfo = new ProcessStartInfo
        {
            FileName = candidate.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var prefix in candidate.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefix);
        }

        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(PythonProbeScript);
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONUTF8"] = "1";

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var errorText = await stderr.ConfigureAwait(false);

            var marker = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(line => line.StartsWith(MarkerPrefix + "JSON:", StringComparison.Ordinal));

            if (marker is null)
            {
                return new PythonProbe(
                    candidate.Display,
                    candidate.FileName,
                    null,
                    null,
                    null,
                    false,
                    0,
                    TrimError(errorText));
            }

            using var document = JsonDocument.Parse(marker[(MarkerPrefix.Length + 5)..]);
            var root = document.RootElement;
            return new PythonProbe(
                candidate.Display,
                GetString(root, "executable") ?? candidate.FileName,
                GetString(root, "python"),
                GetString(root, "torch"),
                GetString(root, "ultralytics"),
                root.TryGetProperty("cuda", out var cuda) && cuda.ValueKind == JsonValueKind.True,
                GetInt(root, "threads", Environment.ProcessorCount),
                GetString(root, "ultralytics_error") ?? GetString(root, "torch_error"));
        }
        catch (OperationCanceledException)
        {
            return new PythonProbe(candidate.Display, candidate.FileName, null, null, null, false, 0, "таймаут проверки");
        }
        catch (Exception exception)
        {
            return new PythonProbe(candidate.Display, candidate.FileName, null, null, null, false, 0, exception.Message);
        }
    }

    private string ResolveBaseModel(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return "yolo26n.pt";
        }

        if (Path.IsPathRooted(configured) && File.Exists(configured))
        {
            return configured;
        }

        var fileName = Path.GetFileName(configured);
        foreach (var root in EnumerateModelRoots())
        {
            var direct = Path.Combine(root, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            try
            {
                var match = Directory
                    .EnumerateFiles(root, "*.pt", SearchOption.AllDirectories)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    return match;
                }
            }
            catch (IOException)
            {
                // папка недоступна — пробуем следующую
            }
        }

        return configured;
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

    private string MakeUniqueRunName(string requested)
    {
        var builder = new StringBuilder();
        foreach (var symbol in requested.Trim())
        {
            builder.Append(char.IsLetterOrDigit(symbol) || symbol is '-' or '_' ? symbol : '-');
        }

        var baseName = builder.ToString().Trim('-', '_');
        if (baseName.Length == 0)
        {
            baseName = "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        var name = baseName;
        var suffix = 2;
        while (Directory.Exists(Path.Combine(_layout.TrainingRuns, name)) &&
               File.Exists(Path.Combine(_layout.TrainingRuns, name, "weights", "best.pt")))
        {
            name = $"{baseName}-{suffix++}";
        }

        return name;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // процесс уже завершился
        }
    }

    private static string Clean(string line)
    {
        var withoutAnsi = AnsiRegex().Replace(line, string.Empty);
        var builder = new StringBuilder(withoutAnsi.Length);
        foreach (var symbol in withoutAnsi)
        {
            if (!char.IsControl(symbol) || symbol == '\t')
            {
                builder.Append(symbol);
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string? TrimError(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= 400 ? trimmed : trimmed[^400..];
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        return null;
    }

    private static int GetInt(JsonElement element, string name, int fallback)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : fallback;

    private static double? GetDouble(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result) ? result : null;

    private sealed record PythonCandidate(string FileName, string[] PrefixArguments, string Display);

    private sealed record PythonProbe(
        string Display,
        string PythonPath,
        string? PythonVersion,
        string? TorchVersion,
        string? UltralyticsVersion,
        bool CudaAvailable,
        int CpuThreads,
        string? Error)
    {
        public TrainingEnvironment ToEnvironment()
        {
            var ready = !string.IsNullOrWhiteSpace(UltralyticsVersion) && !string.IsNullOrWhiteSpace(TorchVersion);
            var message = ready
                ? $"Python {PythonVersion ?? "?"} · ultralytics {UltralyticsVersion} · torch {TorchVersion} · " +
                  (CudaAvailable ? "CUDA" : "CPU")
                : PythonVersion is null
                    ? "Python не запустился: " + (Error ?? "неизвестная ошибка")
                    : $"Python {PythonVersion}: ultralytics/torch не установлены" +
                      (Error is null ? string.Empty : " (" + Error + ")");

            return new TrainingEnvironment(
                ready,
                PythonPath,
                PythonVersion ?? "?",
                UltralyticsVersion ?? "—",
                TorchVersion ?? "—",
                CudaAvailable,
                CpuThreads > 0 ? CpuThreads : Environment.ProcessorCount,
                message);
        }
    }

    private sealed class TrainingState(int totalEpochs)
    {
        private readonly Dictionary<string, double> _metrics = new(StringComparer.OrdinalIgnoreCase);

        public object ProgressGate { get; } = new();

        public object LogGate { get; } = new();

        public DateTime LastProgressAtUtc { get; set; } = DateTime.MinValue;

        public TrainingPhase Phase { get; set; } = TrainingPhase.Training;

        public int Epoch { get; set; }

        public int TotalEpochs { get; set; } = Math.Max(1, totalEpochs);

        public double Percent { get; set; }

        public string? BestWeightsPath { get; set; }

        public string? BestOnnxPath { get; set; }

        public string? RunDirectory { get; set; }

        public string? Error { get; set; }

        public void SetMetrics(JsonElement root)
        {
            if (!root.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in metrics.EnumerateObject())
            {
                if (property.Value.TryGetDouble(out var value))
                {
                    _metrics[property.Name] = value;
                }
            }
        }

        public IReadOnlyDictionary<string, double> MetricsSnapshot()
            => new Dictionary<string, double>(_metrics, StringComparer.OrdinalIgnoreCase);

        public string MetricsText()
        {
            var map50 = Find("metrics/mAP50(B)");
            var map5095 = Find("metrics/mAP50-95(B)");
            if (map50 is null && map5095 is null)
            {
                return "метрики появятся после валидации";
            }

            return string.Create(
                CultureInfo.CurrentCulture,
                $"mAP50 {map50 ?? 0:P0}, mAP50-95 {map5095 ?? 0:P0}");
        }

        private double? Find(string key)
            => _metrics.TryGetValue(key, out var value) ? value : null;
    }

    private const string PythonProbeScript = """
import json, os, platform, sys
info = {"executable": sys.executable, "python": platform.python_version(), "threads": os.cpu_count() or 0,
        "torch": None, "ultralytics": None, "cuda": False}
try:
    import torch
    info["torch"] = torch.__version__
    info["cuda"] = bool(torch.cuda.is_available())
except Exception as exc:
    info["torch_error"] = str(exc)
try:
    import ultralytics
    info["ultralytics"] = ultralytics.__version__
except Exception as exc:
    info["ultralytics_error"] = str(exc)
print("BIRDCAM:JSON:" + json.dumps(info, ensure_ascii=False))
""";
}
