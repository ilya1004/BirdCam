using BirdCam.Application.Detection;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Tests;

public sealed class YoloLabelsTests
{
    [Fact]
    public void Write_ProducesNormalizedBirdLines()
    {
        var birds = new[] { new DetectedBird("bird", 0.9, new BoundingBox(0.1, 0.2, 0.3, 0.4)) };

        var text = YoloLabels.Write(1920, 1080, birds);
        var parts = text.Trim().Split(' ');

        Assert.Equal("0", parts[0]);
        Assert.Equal("0.25", parts[1]);
        Assert.Equal("0.4", parts[2]);
        Assert.Equal("0.3", parts[3]);
        Assert.Equal("0.4", parts[4]);
    }

    [Fact]
    public void Write_ReturnsEmptyForNoBirds()
    {
        Assert.Equal(string.Empty, YoloLabels.Write(1920, 1080, Array.Empty<DetectedBird>()));
    }

    [Fact]
    public void Write_SkipsDegenerateBoxes()
    {
        var birds = new[] { new DetectedBird("bird", 0.9, new BoundingBox(0.1, 0.1, 0, 0.2)) };

        Assert.Equal(string.Empty, YoloLabels.Write(1920, 1080, birds));
    }

    [Fact]
    public void CreateDataYaml_IsPortableAndUsesClassZero()
    {
        var yaml = YoloLabels.CreateDataYaml("images", "images", ["bird"]);

        Assert.Contains("train: 'images'", yaml);
        Assert.Contains("val: 'images'", yaml);
        Assert.Contains("0: 'bird'", yaml);
        Assert.DoesNotContain("path:", yaml);
    }

    [Fact]
    public void CreateDataYaml_WritesExplicitSplitAndClassNames()
    {
        var yaml = YoloLabels.CreateDataYaml("train.txt", "val.txt", ["синица", "воробей"]);

        Assert.Contains("train: 'train.txt'", yaml);
        Assert.Contains("val: 'val.txt'", yaml);
        Assert.Contains("0: 'синица'", yaml);
        Assert.Contains("1: 'воробей'", yaml);
    }

    [Fact]
    public void ParseNames_ReadsUltralyticsMetadata()
    {
        const string metadata = "{0: 'person', 1: 'bicycle', 14: 'bird', 79: 'toothbrush'}";

        var names = YoloLabels.ParseNames(metadata);

        Assert.Equal("person", names[0]);
        Assert.Equal("bird", names[14]);
        Assert.Equal("toothbrush", names[79]);
    }

    [Fact]
    public void ParseNames_ReadsCyrillicAndDoubleQuotes()
    {
        var names = YoloLabels.ParseNames("{0: 'синица', 1: \"воробей\"}");

        Assert.Equal("синица", names[0]);
        Assert.Equal("воробей", names[1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a dict")]
    public void ParseNames_ReturnsEmptyForMissingMetadata(string? metadata)
    {
        Assert.Empty(YoloLabels.ParseNames(metadata));
    }
}
