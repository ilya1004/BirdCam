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
    public void CreateDataYaml_UsesForwardSlashesAndClassZero()
    {
        var yaml = YoloLabels.CreateDataYaml(@"C:\data\birdcam\dataset");

        Assert.Contains("path: C:/data/birdcam/dataset", yaml);
        Assert.Contains("0: bird", yaml);
    }
}
