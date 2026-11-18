using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Detection;

/// <summary>Подавление пересекающихся рамок (greedy NMS).</summary>
public static class Nms
{
    public static IReadOnlyList<int> Select(
        IReadOnlyList<BoundingBox> boxes,
        IReadOnlyList<double> scores,
        double iouThreshold)
    {
        if (boxes.Count != scores.Count)
        {
            throw new ArgumentException("Количество рамок и оценок должно совпадать.", nameof(scores));
        }

        var order = Enumerable.Range(0, boxes.Count)
            .OrderByDescending(index => scores[index])
            .ToArray();

        var kept = new List<int>(boxes.Count);
        var suppressed = new bool[boxes.Count];

        foreach (var index in order)
        {
            if (suppressed[index])
            {
                continue;
            }

            kept.Add(index);

            for (var other = 0; other < boxes.Count; other++)
            {
                if (other == index || suppressed[other])
                {
                    continue;
                }

                if (boxes[index].IntersectionOverUnion(boxes[other]) > iouThreshold)
                {
                    suppressed[other] = true;
                }
            }
        }

        return kept;
    }
}
