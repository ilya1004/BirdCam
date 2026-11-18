namespace BirdCam.Domain.ValueObjects;

/// <summary>
/// Прямоугольник в нормализованных координатах кадра (0..1 по обеим осям).
/// </summary>
public readonly record struct BoundingBox(double X, double Y, double Width, double Height)
{
    public static BoundingBox FromPixels(double x, double y, double width, double height, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0)
        {
            return default;
        }

        return new BoundingBox(
            x / frameWidth,
            y / frameHeight,
            width / frameWidth,
            height / frameHeight).Clamp();
    }

    public BoundingBox Clamp()
    {
        var x = Math.Clamp(X, 0d, 1d);
        var y = Math.Clamp(Y, 0d, 1d);
        var width = Math.Clamp(Width, 0d, 1d - x);
        var height = Math.Clamp(Height, 0d, 1d - y);
        return new BoundingBox(x, y, width, height);
    }

    public (double X, double Y, double Width, double Height) ToPixels(int frameWidth, int frameHeight)
        => (X * frameWidth, Y * frameHeight, Width * frameWidth, Height * frameHeight);

    public BoundingBox Expand(double factor)
    {
        if (factor <= 0)
        {
            return this;
        }

        var growX = Width * factor;
        var growY = Height * factor;
        return new BoundingBox(X - growX, Y - growY, Width + growX * 2, Height + growY * 2).Clamp();
    }

    public double Area => Width * Height;

    public double IntersectionOverUnion(BoundingBox other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);

        if (right <= left || bottom <= top)
        {
            return 0d;
        }

        var intersection = (right - left) * (bottom - top);
        var union = Area + other.Area - intersection;
        return union <= 0 ? 0d : intersection / union;
    }
}
