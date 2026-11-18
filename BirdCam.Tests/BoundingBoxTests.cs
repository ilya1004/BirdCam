using BirdCam.Domain.ValueObjects;

namespace BirdCam.Tests;

public sealed class BoundingBoxTests
{
    [Fact]
    public void Clamp_LimitsCoordinatesToUnitSquare()
    {
        var box = new BoundingBox(0.8, 0.9, 0.5, 0.5).Clamp();

        Assert.Equal(0.8, box.X, 6);
        Assert.Equal(0.9, box.Y, 6);
        Assert.Equal(0.2, box.Width, 6);
        Assert.Equal(0.1, box.Height, 6);
    }

    [Fact]
    public void IntersectionOverUnion_ComputesOverlap()
    {
        var a = new BoundingBox(0, 0, 0.5, 0.5);
        var b = new BoundingBox(0.25, 0.25, 0.5, 0.5);

        Assert.Equal(0.142857, a.IntersectionOverUnion(b), 5);
    }

    [Fact]
    public void FromPixels_NormalizesUsingFrameSize()
    {
        var box = BoundingBox.FromPixels(96, 54, 192, 108, 1920, 1080);

        Assert.Equal(0.05, box.X, 6);
        Assert.Equal(0.05, box.Y, 6);
        Assert.Equal(0.1, box.Width, 6);
        Assert.Equal(0.1, box.Height, 6);
    }

    [Fact]
    public void Expand_AddsPaddingAndClamps()
    {
        var box = new BoundingBox(0.05, 0.05, 0.2, 0.2).Expand(0.5);

        Assert.Equal(0, box.X, 6);
        Assert.Equal(0, box.Y, 6);
        Assert.Equal(0.4, box.Width, 6);
        Assert.Equal(0.4, box.Height, 6);
    }
}

