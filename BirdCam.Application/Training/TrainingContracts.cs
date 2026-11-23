namespace BirdCam.Application.Training;

/// <summary>Результат проверки датасета перед обучением.</summary>
public sealed record TrainingAudit(
    int ImageCount,
    int LabelCount,
    int BoxCount,
    int CropCount,
    int ImagesWithoutLabels,
    int OrphanLabels,
    int InvalidLabelLines,
    int EmptyLabelFiles,
    int OutOfRangeBoxes,
    long TotalImageBytes,
    DateTimeOffset? FirstImageAt,
    DateTimeOffset? LastImageAt,
    IReadOnlyDictionary<int, int> ClassCounts,
    IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => ImageCount == 0;

    public bool HasUsableImages => ImageCount > 0 && ImageCount - ImagesWithoutLabels > 0;

    public int MaxClassId => ClassCounts.Count == 0 ? -1 : ClassCounts.Keys.Max();
}

/// <summary>Параметры подготовки train/val-сплита и data.yaml.</summary>
public sealed record TrainingSplitOptions(
    IReadOnlyList<string> ClassNames,
    int ValidationPercent = 20,
    int Seed = 1337);

/// <summary>Результат подготовки сплита.</summary>
public sealed record TrainingSplitResult(
    string DataYamlPath,
    string TrainListPath,
    string ValListPath,
    int TrainImages,
    int ValImages,
    IReadOnlyList<string> ClassNames,
    DateTimeOffset? TrainLastAt,
    DateTimeOffset? ValFirstAt,
    string Message);

/// <summary>Окружение Python, найденное на машине.</summary>
public sealed record TrainingEnvironment(
    bool IsReady,
    string PythonPath,
    string PythonVersion,
    string UltralyticsVersion,
    string TorchVersion,
    bool CudaAvailable,
    int CpuThreads,
    string Message,
    string GpuName = "",
    string HipVersion = "");

/// <summary>Параметры запуска обучения.</summary>
public sealed record TrainingRunOptions(
    string DataYamlPath,
    string BaseModel,
    string RunName,
    int Epochs,
    int ImageSize,
    int Batch,
    string Device,
    int Workers,
    bool ExportOnnx);

public enum TrainingPhase
{
    Idle,
    Preparing,
    Setup,
    Training,
    Exporting,
    Completed,
    Failed,
    Canceled
}

/// <summary>Одно событие прогресса обучения. IsLog — строка сырого вывода Python.</summary>
public sealed record TrainingProgress(
    TrainingPhase Phase,
    string Message,
    int Epoch = 0,
    int Epochs = 0,
    double Percent = 0,
    IReadOnlyDictionary<string, double>? Metrics = null,
    bool IsLog = false);

/// <summary>Итог обучения.</summary>
public sealed record TrainingResult(
    bool Success,
    bool Canceled,
    string? RunDirectory,
    string? BestWeightsPath,
    string? BestOnnxPath,
    string? LogPath,
    string? Error,
    IReadOnlyDictionary<string, double>? Metrics)
{
    public static TrainingResult Fail(string error, string? logPath = null)
        => new(false, false, null, null, null, logPath, error, null);

    public static TrainingResult Cancelled(string? logPath = null)
        => new(false, true, null, null, null, logPath, null, null);
}

/// <summary>Установленная в приложение модель.</summary>
public sealed record ModelInstallResult(string ModelFileName, string ModelPath, string ClassName);

/// <summary>Транслитерация названий для имён файлов и папок запусков.</summary>
public static class TrainingSlug
{
    private static readonly (char Source, string Target)[] Map =
    [
        ('а', "a"), ('б', "b"), ('в', "v"), ('г', "g"), ('д', "d"), ('е', "e"), ('ё', "e"),
        ('ж', "zh"), ('з', "z"), ('и', "i"), ('й', "y"), ('к', "k"), ('л', "l"), ('м', "m"),
        ('н', "n"), ('о', "o"), ('п', "p"), ('р', "r"), ('с', "s"), ('т', "t"), ('у', "u"),
        ('ф', "f"), ('х', "h"), ('ц', "ts"), ('ч', "ch"), ('ш', "sh"), ('щ', "sch"), ('ъ', ""),
        ('ы', "y"), ('ь', ""), ('э', "e"), ('ю', "yu"), ('я', "ya")
    ];

    /// <summary>ASCII-имя без пробелов и спецсимволов; пустой результат заменяется на species.</summary>
    public static string From(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "species";
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        foreach (var raw in value.Trim().ToLowerInvariant())
        {
            if (raw is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                builder.Append(raw);
                continue;
            }

            if (raw is '-' or '_')
            {
                builder.Append(raw);
                continue;
            }

            var mapped = Map.FirstOrDefault(item => item.Source == raw).Target;
            if (!string.IsNullOrEmpty(mapped))
            {
                builder.Append(mapped);
            }
            else if (char.IsWhiteSpace(raw) || raw is '.' or ',' or '/')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-', '_', '.');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Length == 0 ? "species" : slug[..Math.Min(slug.Length, 48)];
    }
}
