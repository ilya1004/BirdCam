using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Detection;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using Microsoft.Extensions.Logging;

namespace BirdCam.Infrastructure.Training;

/// <summary>
/// Читает датасет BirdCam (images + labels), проверяет разметку и готовит
/// train.txt/val.txt и data.yaml для ultralytics. Сплит по времени:
/// последние ValidationPercent кадров уходят в валидацию.
/// </summary>
public sealed partial class TrainingDataService : ITrainingDataService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".webp"
    };

    private readonly StorageLayout _layout;
    private readonly ILogger<TrainingDataService> _logger;

    public TrainingDataService(StorageLayout layout, ILogger<TrainingDataService> logger)
    {
        _layout = layout;
        _logger = logger;
    }

    [GeneratedRegex(@"^(?<date>\d{8})_(?<time>\d{6})")]
    private static partial Regex TimestampRegex();

    public Task<TrainingAudit> AuditAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => AuditCore(cancellationToken), cancellationToken);

    public Task<TrainingSplitResult> PrepareSplitAsync(
        TrainingSplitOptions options,
        CancellationToken cancellationToken = default)
        => Task.Run(() => PrepareCore(options, cancellationToken), cancellationToken);

    private TrainingAudit AuditCore(CancellationToken cancellationToken)
    {
        var images = EnumerateImages();
        var labels = EnumerateLabels();
        var labelNames = labels.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var imageNames = images.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var classCounts = new Dictionary<int, int>();
        var warnings = new List<string>();
        var boxCount = 0;
        var imagesWithoutLabels = 0;
        var invalidLines = 0;
        var emptyLabels = 0;
        var outOfRange = 0;
        long totalBytes = 0;
        DateTimeOffset? firstAt = null;
        DateTimeOffset? lastAt = null;

        foreach (var image in images)
        {
            cancellationToken.ThrowIfCancellationRequested();

            totalBytes += SafeLength(image);
            var stamp = ParseTimestamp(Path.GetFileNameWithoutExtension(image));
            if (stamp is not null)
            {
                firstAt = firstAt is null || stamp < firstAt ? stamp : firstAt;
                lastAt = lastAt is null || stamp > lastAt ? stamp : lastAt;
            }

            var labelPath = Path.Combine(_layout.DatasetLabels, Path.GetFileNameWithoutExtension(image) + ".txt");
            if (!File.Exists(labelPath))
            {
                imagesWithoutLabels++;
                continue;
            }

            var lines = ReadLabelLines(labelPath);
            if (lines.Count == 0)
            {
                emptyLabels++;
                continue;
            }

            foreach (var line in lines)
            {
                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 5 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var classId))
                {
                    invalidLines++;
                    continue;
                }

                var values = new double[4];
                var parsed = true;
                for (var index = 0; index < values.Length; index++)
                {
                    if (!double.TryParse(parts[index + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out values[index]))
                    {
                        parsed = false;
                        break;
                    }
                }

                if (!parsed)
                {
                    invalidLines++;
                    continue;
                }

                boxCount++;
                classCounts[classId] = classCounts.GetValueOrDefault(classId) + 1;

                var (centerX, centerY, width, height) = (values[0], values[1], values[2], values[3]);
                if (width <= 0 || height <= 0 || width > 1.0001 || height > 1.0001 ||
                    centerX < 0 || centerX > 1 || centerY < 0 || centerY > 1)
                {
                    outOfRange++;
                }
            }
        }

        var orphanLabels = labelNames.Count(name => !imageNames.Contains(name));

        if (images.Count == 0)
        {
            warnings.Add("В папке dataset/images нет изображений.");
        }

        if (imagesWithoutLabels > 0)
        {
            warnings.Add($"Без разметки: {imagesWithoutLabels} кадров.");
        }

        if (orphanLabels > 0)
        {
            warnings.Add($"Разметка без изображений: {orphanLabels} файлов.");
        }

        if (invalidLines > 0)
        {
            warnings.Add($"Некорректных строк разметки: {invalidLines}.");
        }

        if (outOfRange > 0)
        {
            warnings.Add($"Координаты вне [0,1]: {outOfRange} рамок.");
        }

        if (emptyLabels > 0)
        {
            warnings.Add($"Пустых файлов разметки: {emptyLabels}.");
        }

        if (boxCount == 0 && images.Count > 0)
        {
            warnings.Add("В разметке нет ни одной рамки — обучать детектор не на чем.");
        }

        var cropCount = Directory.Exists(_layout.DatasetCrops)
            ? Directory.EnumerateFiles(_layout.DatasetCrops, "*", SearchOption.AllDirectories).Count()
            : 0;

        return new TrainingAudit(
            images.Count,
            labels.Count,
            boxCount,
            cropCount,
            imagesWithoutLabels,
            orphanLabels,
            invalidLines,
            emptyLabels,
            outOfRange,
            totalBytes,
            firstAt,
            lastAt,
            classCounts,
            warnings);
    }

    private TrainingSplitResult PrepareCore(TrainingSplitOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var audit = AuditCore(cancellationToken);
        if (audit.IsEmpty)
        {
            throw new InvalidOperationException("В датасете нет изображений: добавьте примеры на вкладке «Датасет».");
        }

        if (audit.BoxCount == 0)
        {
            throw new InvalidOperationException("В датасете нет размеченных рамок.");
        }

        if (audit.InvalidLabelLines > 0 || audit.OutOfRangeBoxes > 0)
        {
            throw new InvalidOperationException(
                "Разметка содержит ошибки. Исправьте labels/*.txt или удалите проблемные файлы, затем повторите.");
        }

        var classNames = NormalizeClassNames(options.ClassNames);
        if (classNames.Count == 0)
        {
            throw new InvalidOperationException("Укажите хотя бы одно название класса.");
        }

        if (classNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Названия классов не должны быть пустыми: по одному названию на строку.");
        }

        if (audit.MaxClassId >= classNames.Count)
        {
            throw new InvalidOperationException(
                $"В разметке есть класс {audit.MaxClassId}, а названий классов задано {classNames.Count}. " +
                "Добавьте название для каждого класса (по одному на строку).");
        }

        var usable = new List<(string Image, string Label, DateTimeOffset? Stamp)>();
        var skippedEmpty = 0;
        foreach (var image in EnumerateImages())
        {
            var name = Path.GetFileNameWithoutExtension(image);
            var label = Path.Combine(_layout.DatasetLabels, name + ".txt");
            if (!File.Exists(label))
            {
                continue;
            }

            if (ReadLabelLines(label).Count == 0)
            {
                skippedEmpty++;
                continue;
            }

            usable.Add((image, label, ParseTimestamp(name)));
        }

        if (usable.Count < 4)
        {
            throw new InvalidOperationException(
                $"Для обучения нужно минимум 4 размеченных кадра, сейчас {usable.Count}.");
        }

        var ordered = usable
            .OrderBy(item => item.Stamp ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.Image, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var percent = Math.Clamp(options.ValidationPercent, 5, 50);
        var valCount = Math.Clamp((int)Math.Round(ordered.Count * percent / 100d), 1, ordered.Count - 1);
        var timeOrdered = ordered.All(item => item.Stamp is not null);

        List<(string Image, string Label, DateTimeOffset? Stamp)> train;
        List<(string Image, string Label, DateTimeOffset? Stamp)> val;

        if (timeOrdered)
        {
            train = ordered.Take(ordered.Count - valCount).ToList();
            val = ordered.Skip(ordered.Count - valCount).ToList();
        }
        else
        {
            var random = new Random(options.Seed);
            var shuffled = ordered.OrderBy(_ => random.Next()).ToList();
            val = shuffled.Take(valCount).ToList();
            train = shuffled.Skip(valCount).ToList();
        }

        var trainListPath = Path.Combine(_layout.Dataset, "train.txt");
        var valListPath = Path.Combine(_layout.Dataset, "val.txt");
        var dataYamlPath = Path.Combine(_layout.Dataset, "data.yaml");

        File.WriteAllLines(
            trainListPath,
            train.Select(item => "./images/" + Path.GetFileName(item.Image)),
            new UTF8Encoding(false));
        File.WriteAllLines(
            valListPath,
            val.Select(item => "./images/" + Path.GetFileName(item.Image)),
            new UTF8Encoding(false));
        File.WriteAllText(
            dataYamlPath,
            YoloLabels.CreateDataYaml("train.txt", "val.txt", classNames),
            new UTF8Encoding(false));

        DeleteCacheFile("labels.cache");
        DeleteCacheFile("images.cache");

        var message = timeOrdered
            ? $"train {train.Count}, val {val.Count}: последние {valCount} кадров по времени"
            : $"train {train.Count}, val {val.Count}: случайный сплит (seed {options.Seed})";

        if (skippedEmpty > 0)
        {
            message += $"; пропущено кадров без рамок: {skippedEmpty}";
        }

        _logger.LogInformation(
            "Сплит датасета готов: {Message}, классов {Classes}, data.yaml: {Yaml}",
            message,
            classNames.Count,
            dataYamlPath);

        return new TrainingSplitResult(
            dataYamlPath,
            trainListPath,
            valListPath,
            train.Count,
            val.Count,
            classNames,
            train.Select(item => item.Stamp).Max(),
            val.Select(item => item.Stamp).Min(),
            message);
    }

    private List<string> EnumerateImages()
    {
        if (!Directory.Exists(_layout.DatasetImages))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_layout.DatasetImages, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<string> EnumerateLabels()
    {
        if (!Directory.Exists(_layout.DatasetLabels))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(_layout.DatasetLabels, "*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ReadLabelLines(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static List<string> NormalizeClassNames(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var name in names)
        {
            result.Add((name ?? string.Empty).Trim());
        }

        return result;
    }

    private static DateTimeOffset? ParseTimestamp(string fileName)
    {
        var match = TimestampRegex().Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        var text = match.Groups["date"].Value + match.Groups["time"].Value;
        if (!DateTime.TryParseExact(
                text,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var local))
        {
            return null;
        }

        return new DateTimeOffset(local);
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private void DeleteCacheFile(string name)
    {
        try
        {
            var path = Path.Combine(_layout.Dataset, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            _logger.LogDebug(exception, "Не удалось удалить кэш {Cache}", name);
        }
    }
}
