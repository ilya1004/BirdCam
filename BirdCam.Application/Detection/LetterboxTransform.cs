using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Detection;

/// <summary>
/// Геометрия letterbox-преобразования: как кадр вписывается в квадрат модели и как
/// координаты детекций возвращаются обратно в координаты исходного кадра.
/// </summary>
public readonly record struct LetterboxTransform(float Scale, int PadX, int PadY, int SourceWidth, int SourceHeight)
{
    public static LetterboxTransform Create(int sourceWidth, int sourceHeight, int targetSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceHeight, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetSize, 1);

        var scale = Math.Min((float)targetSize / sourceWidth, (float)targetSize / sourceHeight);
        var scaledWidth = (int)MathF.Round(sourceWidth * scale);
        var scaledHeight = (int)MathF.Round(sourceHeight * scale);
        var padX = (targetSize - scaledWidth) / 2;
        var padY = (targetSize - scaledHeight) / 2;

        return new LetterboxTransform(scale, padX, padY, sourceWidth, sourceHeight);
    }

    public BoundingBox ToSource(double centerX, double centerY, double width, double height)
    {
        if (Scale <= 0)
        {
            return default;
        }

        var x = (centerX - width / 2d - PadX) / Scale;
        var y = (centerY - height / 2d - PadY) / Scale;
        var w = width / Scale;
        var h = height / Scale;

        return BoundingBox.FromPixels(x, y, w, h, SourceWidth, SourceHeight);
    }
}
