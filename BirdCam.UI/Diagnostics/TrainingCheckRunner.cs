using System.IO;
using System.Text;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using BirdCam.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace BirdCam.UI.Diagnostics;

/// <summary>
/// Проверка обучения без UI: аудит датасета, подготовка сплита и наличие Python/ultralytics.
/// Результат пишется в logs/training-check.log.
/// </summary>
internal static class TrainingCheckRunner
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        StorageLayout layout,
        AppSettings settings,
        string[] args)
    {
        var log = new StringBuilder();
        var training = services.GetRequiredService<ITrainingService>();
        var exitCode = 0;

        void Write(string line)
        {
            log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");
            Console.WriteLine(line);
        }

        try
        {
            Write($"storage: {layout.Root}");
            Write($"training: {training.TrainingDirectory}");

            var installPath = GetOption(args, "--install");
            if (!string.IsNullOrWhiteSpace(installPath))
            {
                var className = (settings.Training?.ClassNames ?? "синица")
                    .Split('\n', StringSplitOptions.TrimEntries)
                    .FirstOrDefault(line => line.Length > 0) ?? "синица";
                var installed = await training.InstallAsync(installPath, className);
                Write($"install: {installed.ModelFileName} -> {installed.ModelPath}");
                Write($"install: класс «{installed.ClassName}», birdClassIds=0");
                exitCode = 0;
                Write("RESULT: INSTALL OK");
                return exitCode;
            }

            var audit = await training.AuditAsync();
            Write($"dataset: images={audit.ImageCount}, labels={audit.LabelCount}, boxes={audit.BoxCount}, crops={audit.CropCount}");
            Write($"dataset: missingLabels={audit.ImagesWithoutLabels}, invalid={audit.InvalidLabelLines}, empty={audit.EmptyLabelFiles}, outOfRange={audit.OutOfRangeBoxes}");
            Write($"dataset: classes={string.Join(", ", audit.ClassCounts.Select(pair => $"{pair.Key}={pair.Value}"))}");
            Write($"dataset: range={audit.FirstImageAt:dd.MM.yyyy HH:mm} — {audit.LastImageAt:dd.MM.yyyy HH:mm}");

            foreach (var warning in audit.Warnings)
            {
                Write("warning: " + warning);
            }

            var names = (settings.Training?.ClassNames ?? string.Empty)
                .Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.Length > 0)
                .ToList();

            if (names.Count == 0)
            {
                names = ["синица"];
            }

            var split = await training.PrepareSplitAsync(new TrainingSplitOptions(names, 20));
            Write($"split: {split.Message}");
            Write($"data.yaml: {split.DataYamlPath}");
            Write($"train list: {split.TrainListPath} ({split.TrainImages})");
            Write($"val list: {split.ValListPath} ({split.ValImages})");

            var environment = await training.DetectEnvironmentAsync();
            Write($"python: {(environment.IsReady ? "OK" : "FAIL")} — {environment.Message}");
            Write($"python path: {environment.PythonPath}");
            Write($"ultralytics: {environment.UltralyticsVersion}, torch: {environment.TorchVersion}, cuda={environment.CudaAvailable}");

            exitCode = audit.HasUsableImages && environment.IsReady ? 0 : 1;
            Write(exitCode == 0 ? "RESULT: OK" : "RESULT: FAIL");

            if (args.Contains("--training-ui", StringComparer.OrdinalIgnoreCase) && exitCode == 0)
            {
                var viewModel = services.GetRequiredService<TrainingViewModel>();
                await viewModel.EnsureLoadedAsync();

                viewModel.Epochs = GetIntOption(args, "--epochs", settings.Training?.Epochs ?? 40);
                viewModel.ImageSize = GetIntOption(args, "--imgsz", settings.Training?.ImageSize ?? 640);
                viewModel.Batch = GetIntOption(args, "--batch", settings.Training?.Batch ?? 8);
                viewModel.Workers = GetIntOption(args, "--workers", settings.Training?.Workers ?? 2);
                viewModel.SelectedModel = viewModel.AvailableModels
                    .FirstOrDefault(model => model.Name.StartsWith("yolo26n", StringComparison.OrdinalIgnoreCase))
                    ?? viewModel.SelectedModel;

                Write($"training-ui: {viewModel.Epochs} эпох, {viewModel.ImageSize}px, batch {viewModel.Batch}, модель {viewModel.SelectedModel?.Name}");

                if (!viewModel.StartTrainingCommand.CanExecute(null))
                {
                    Write("training-ui: команда недоступна (нет окружения или идёт обучение)");
                    exitCode = 4;
                }
                else
                {
                    viewModel.StartTrainingCommand.Execute(null);
                    if (viewModel.StartTrainingCommand is IAsyncRelayCommand asyncCommand &&
                        asyncCommand.ExecutionTask is { } execution)
                    {
                        await execution;
                    }

                    Write($"training-ui: hasResult={viewModel.HasResult}");
                    Write("training-ui: " + viewModel.ResultText.Replace("\n", " | "));
                    Write("training-ui: status=" + viewModel.StatusText);
                    Write("training-ui: metrics=" + viewModel.MetricsText);

                    Directory.CreateDirectory(layout.Logs);
                    await File.WriteAllTextAsync(
                        Path.Combine(layout.Logs, "training-ui.log"),
                        viewModel.LogText);

                    exitCode = viewModel.HasResult ? 0 : 5;
                    Write(exitCode == 0 ? "RESULT: UI TRAIN OK" : "RESULT: UI TRAIN FAIL");
                }
            }

            if (args.Contains("--training-smoke", StringComparer.OrdinalIgnoreCase) && exitCode == 0)
            {
                Write("training-smoke: 1 эпоха, 320px, batch 4");
                var runName = "smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var progress = new InlineProgress<TrainingProgress>(item =>
                    Write((item.IsLog ? "log: " : $"progress[{item.Phase}] ") + item.Message));

                var result = await training.TrainAsync(
                    new TrainingRunOptions(
                        split.DataYamlPath,
                        "yolo26n.pt",
                        runName,
                        Epochs: 1,
                        ImageSize: 320,
                        Batch: 4,
                        Device: "cpu",
                        Workers: 0,
                        ExportOnnx: true),
                    progress);

                Write($"train: success={result.Success}, canceled={result.Canceled}, error={result.Error}");
                Write($"train: best={result.BestWeightsPath}");
                Write($"train: onnx={result.BestOnnxPath}");
                Write($"train: log={result.LogPath}");
                exitCode = result.Success && result.BestOnnxPath is not null ? 0 : 3;
                Write(exitCode == 0 ? "RESULT: TRAIN OK" : "RESULT: TRAIN FAIL");
            }
            else if (exitCode == 0)
            {
                Write("training-smoke: пропущен (укажите --training-smoke для 1 эпохи)");
            }
        }
        catch (Exception exception)
        {
            Write("RESULT: FAIL " + exception);
            exitCode = 2;
        }
        finally
        {
            Directory.CreateDirectory(layout.Logs);
            await File.WriteAllTextAsync(Path.Combine(layout.Logs, "training-check.log"), log.ToString());
        }

        return exitCode;
    }

    private static int GetIntOption(string[] args, string name, int fallback)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}
