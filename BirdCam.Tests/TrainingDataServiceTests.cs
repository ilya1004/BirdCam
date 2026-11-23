using System.Text;
using BirdCam.Application.Storage;
using BirdCam.Application.Training;
using BirdCam.Infrastructure.Training;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirdCam.Tests;

public sealed class TrainingDataServiceTests : IDisposable
{
    private readonly string _root;
    private readonly StorageLayout _layout;
    private readonly TrainingDataService _service;

    public TrainingDataServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "birdcam-training-tests", Guid.NewGuid().ToString("N"));
        _layout = new StorageLayout(_root);
        _layout.EnsureCreated();
        _service = new TrainingDataService(_layout, NullLogger<TrainingDataService>.Instance);
    }

    [Fact]
    public async Task Audit_CountsImagesLabelsBoxesAndWarnings()
    {
        AddSample("20260101_120000_000_aaaaaa", "0 0.5 0.5 0.2 0.2\n0 0.2 0.2 0.1 0.1");
        AddSample("20260101_120030_000_bbbbbb", "0 0.4 0.4 0.2 0.2");
        AddSample("20260101_120100_000_cccccc", string.Empty);
        File.WriteAllBytes(Path.Combine(_layout.DatasetImages, "missing.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
        File.WriteAllText(Path.Combine(_layout.DatasetLabels, "orphan.txt"), "0 0.5 0.5 0.1 0.1");

        var audit = await _service.AuditAsync();

        Assert.Equal(4, audit.ImageCount);
        Assert.Equal(4, audit.LabelCount);
        Assert.Equal(3, audit.BoxCount);
        Assert.Equal(1, audit.EmptyLabelFiles);
        Assert.Equal(1, audit.OrphanLabels);
        Assert.Equal(3, audit.ClassCounts[0]);
        Assert.Equal(1, audit.ImagesWithoutLabels);
        Assert.Contains(audit.Warnings, warning => warning.Contains("Пустых", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_ReportsMalformedAndOutOfRangeLines()
    {
        AddSample("20260101_120000_000_aaaaaa", "0 0.5 0.5\n0 1.4 0.5 0.2 0.2\n1 0.5 0.5 0.2 0.2");

        var audit = await _service.AuditAsync();

        Assert.Equal(1, audit.InvalidLabelLines);
        Assert.Equal(1, audit.OutOfRangeBoxes);
        Assert.Equal(2, audit.BoxCount);
        Assert.Equal(1, audit.ClassCounts.Keys.Max());
    }

    [Fact]
    public async Task PrepareSplit_WritesTimeOrderedListsAndYaml()
    {
        for (var index = 0; index < 10; index++)
        {
            var stamp = new DateTime(2026, 1, 1, 12, index, 0).ToString("yyyyMMdd_HHmmss");
            AddSample($"{stamp}_000_{index:000000}", "0 0.5 0.5 0.2 0.2");
        }

        var result = await _service.PrepareSplitAsync(new TrainingSplitOptions(["синица"], 20));

        Assert.Equal(8, result.TrainImages);
        Assert.Equal(2, result.ValImages);
        Assert.True(File.Exists(result.DataYamlPath));

        var train = await File.ReadAllLinesAsync(result.TrainListPath);
        var val = await File.ReadAllLinesAsync(result.ValListPath);

        Assert.Equal(8, train.Length);
        Assert.Equal(2, val.Length);
        Assert.Empty(train.Intersect(val, StringComparer.OrdinalIgnoreCase));
        Assert.EndsWith("20260101_120900_000_000009.jpg", val[^1], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 1, 1, 12, 8, 0)), result.ValFirstAt);

        var yaml = await File.ReadAllTextAsync(result.DataYamlPath);
        Assert.Contains("train: 'train.txt'", yaml);
        Assert.Contains("val: 'val.txt'", yaml);
        Assert.Contains("0: 'синица'", yaml);
    }

    [Fact]
    public async Task PrepareSplit_SkipsEmptyLabels()
    {
        AddSample("20260101_120000_000_aaaaaa", "0 0.5 0.5 0.2 0.2");
        AddSample("20260101_120030_000_bbbbbb", "0 0.5 0.5 0.2 0.2");
        AddSample("20260101_120100_000_cccccc", "0 0.5 0.5 0.2 0.2");
        AddSample("20260101_120130_000_dddddd", "0 0.5 0.5 0.2 0.2");
        AddSample("20260101_120200_000_eeeeee", string.Empty);

        var result = await _service.PrepareSplitAsync(new TrainingSplitOptions(["синица"], 20));

        Assert.Equal(4, result.TrainImages + result.ValImages);
        Assert.Contains("пропущено кадров без рамок: 1", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareSplit_RejectsMissingClassName()
    {
        AddSample("20260101_120000_000_aaaaaa", "1 0.5 0.5 0.2 0.2");
        AddSample("20260101_120030_000_bbbbbb", "1 0.5 0.5 0.2 0.2");
        AddSample("20260101_120100_000_cccccc", "1 0.5 0.5 0.2 0.2");
        AddSample("20260101_120130_000_dddddd", "1 0.5 0.5 0.2 0.2");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PrepareSplitAsync(new TrainingSplitOptions(["синица"], 20)));

        Assert.Contains("класс 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareSplit_RejectsEmptyClassName()
    {
        for (var index = 0; index < 4; index++)
        {
            AddSample($"20260101_12000{index}_000_{index:000000}", "0 0.5 0.5 0.2 0.2");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.PrepareSplitAsync(new TrainingSplitOptions([" "], 20)));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временная папка может остаться
        }
    }

    private void AddSample(string name, string label)
    {
        var imagePath = Path.Combine(_layout.DatasetImages, name + ".jpg");
        File.WriteAllBytes(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
        File.WriteAllText(
            Path.Combine(_layout.DatasetLabels, name + ".txt"),
            label,
            new UTF8Encoding(false));
    }
}
