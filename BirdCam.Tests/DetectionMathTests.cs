using BirdCam.Application.Detection;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Tests;

public sealed class DetectionMathTests
{
    [Fact]
    public void Letterbox_ComputesPadsForWideFrame()
    {
        var transform = LetterboxTransform.Create(1920, 1080, 640);

        Assert.Equal(640, Math.Round(1920 * transform.Scale), 3);
        Assert.Equal(360, Math.Round(1080 * transform.Scale), 3);
        Assert.Equal(0, transform.PadX);
        Assert.Equal(140, transform.PadY);
    }

    [Fact]
    public void Letterbox_MapsBoxBackToSourcePixels()
    {
        var transform = LetterboxTransform.Create(1920, 1080, 640);
        var box = transform.ToSource(320, 320, 64, 64);

        Assert.Equal(0.45, box.X, 3);
        Assert.Equal(0.411, box.Y, 3);
        Assert.Equal(0.1, box.Width, 3);
        Assert.Equal(0.178, box.Height, 3);
    }

    [Fact]
    public void Letterbox_ClampsBoxesOutsideFrame()
    {
        var transform = LetterboxTransform.Create(640, 480, 640);
        var box = transform.ToSource(-50, -50, 100, 100);

        Assert.True(box.X >= 0);
        Assert.True(box.Y >= 0);
        Assert.True(box.X + box.Width <= 1.0000001);
        Assert.True(box.Y + box.Height <= 1.0000001);
    }

    [Fact]
    public void Nms_SuppressesOverlappingBoxes()
    {
        var boxes = new List<BoundingBox>
        {
            new(0.00, 0.00, 0.50, 0.50),
            new(0.05, 0.05, 0.50, 0.50),
            new(0.50, 0.50, 0.40, 0.40)
        };
        var scores = new List<double> { 0.90, 0.80, 0.70 };

        var kept = Nms.Select(boxes, scores, 0.45);

        Assert.Equal(2, kept.Count);
        Assert.Contains(0, kept);
        Assert.Contains(2, kept);
        Assert.DoesNotContain(1, kept);
    }

    [Fact]
    public void Nms_ThrowsWhenInputsDiffer()
    {
        var boxes = new List<BoundingBox> { new(0, 0, 1, 1) };
        var scores = new List<double> { 0.5, 0.6 };

        Assert.Throws<ArgumentException>(() => Nms.Select(boxes, scores, 0.5));
    }
}
