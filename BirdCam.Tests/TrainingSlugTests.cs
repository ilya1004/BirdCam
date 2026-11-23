using BirdCam.Application.Training;

namespace BirdCam.Tests;

public sealed class TrainingSlugTests
{
    [Theory]
    [InlineData("синица", "sinitsa")]
    [InlineData("Воробей", "vorobey")]
    [InlineData("Bird Cam", "bird-cam")]
    [InlineData("  синица  ", "sinitsa")]
    [InlineData("", "species")]
    [InlineData("!!!", "species")]
    public void From_ProducesSafeAsciiName(string input, string expected)
    {
        Assert.Equal(expected, TrainingSlug.From(input));
    }
}
