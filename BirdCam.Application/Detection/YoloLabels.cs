using System.Globalization;
using System.Text;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Detection;

/// <summary>Разметка в формате YOLO (класс 0 — птица).</summary>
public static class YoloLabels
{
    public const int BirdClassId = 0;

    public static string Write(int frameWidth, int frameHeight, IReadOnlyList<DetectedBird> birds)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        foreach (var bird in birds)
        {
            var box = bird.Box.Clamp();
            if (box.Width <= 0 || box.Height <= 0)
            {
                continue;
            }

            var centerX = box.X + box.Width / 2d;
            var centerY = box.Y + box.Height / 2d;

            builder.Append(BirdClassId.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(Format(centerX)).Append(' ')
                .Append(Format(centerY)).Append(' ')
                .Append(Format(box.Width)).Append(' ')
                .Append(Format(box.Height)).AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>data.yaml для дообучения через ultralytics.</summary>
    public static string CreateDataYaml(string datasetAbsolutePath)
    {
        var normalized = datasetAbsolutePath.Replace('\\', '/');
        return $"""
                path: {normalized}
                train: images
                val: images
                names:
                  0: bird
                """;
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
