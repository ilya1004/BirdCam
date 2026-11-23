using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Detection;

/// <summary>Разметка в формате YOLO (класс 0 — птица).</summary>
public static partial class YoloLabels
{
    public const int BirdClassId = 0;

    [GeneratedRegex(@"(?<id>\d+)\s*:\s*(?:'(?<single>[^']*)'|""(?<double>[^""]*)""|(?<bare>[^,}]+))")]
    private static partial Regex NamesRegex();

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

    /// <summary>
    /// data.yaml с относительными списками кадров и названиями классов.
    /// path не задаётся: ultralytics берёт папку самого yaml, поэтому датасет
    /// переносим (ZIP можно распаковать и обучить в Colab).
    /// </summary>
    public static string CreateDataYaml(
        string train,
        string val,
        IReadOnlyList<string> classNames)
    {
        var builder = new StringBuilder();
        builder.Append("train: ").Append(YamlString(train)).AppendLine();
        builder.Append("val: ").Append(YamlString(val)).AppendLine();
        builder.AppendLine("names:");

        for (var index = 0; index < classNames.Count; index++)
        {
            builder.Append("  ").Append(index).Append(": ")
                .Append(YamlString(classNames[index] ?? string.Empty)).AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// Разбирает metadata «names» из ONNX-модели ultralytics.
    /// Формат — Python-dict: {0: 'синица', 1: 'воробей'}.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ParseNames(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return new Dictionary<int, string>();
        }

        var names = new Dictionary<int, string>();
        foreach (Match match in NamesRegex().Matches(metadata))
        {
            if (!int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var value = match.Groups["single"].Success
                ? match.Groups["single"].Value
                : match.Groups["double"].Success
                    ? match.Groups["double"].Value
                    : match.Groups["bare"].Value.Trim();

            value = value.Trim().Trim('\'').Trim('"').Trim();
            if (value.Length > 0)
            {
                names[id] = value;
            }
        }

        return names;
    }

    private static string YamlString(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
