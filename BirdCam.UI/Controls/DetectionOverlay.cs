using System.Globalization;
using System.Windows;
using System.Windows.Media;
using BirdCam.Domain.ValueObjects;

namespace BirdCam.UI.Controls;

/// <summary>Рисует рамки детекций поверх видео с учётом Uniform-масштабирования.</summary>
public sealed class DetectionOverlay : FrameworkElement
{
    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items),
        typeof(IReadOnlyList<DetectedBird>),
        typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(
        nameof(SourceWidth),
        typeof(double),
        typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(
        nameof(SourceHeight),
        typeof(double),
        typeof(DetectionOverlay),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Pen BoxPen = CreatePen();

    public IReadOnlyList<DetectedBird>? Items
    {
        get => (IReadOnlyList<DetectedBird>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public double SourceWidth
    {
        get => (double)GetValue(SourceWidthProperty);
        set => SetValue(SourceWidthProperty, value);
    }

    public double SourceHeight
    {
        get => (double)GetValue(SourceHeightProperty);
        set => SetValue(SourceHeightProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var items = Items;
        if (items is null || items.Count == 0 || SourceWidth <= 0 || SourceHeight <= 0 ||
            ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(ActualWidth / SourceWidth, ActualHeight / SourceHeight);
        var offsetX = (ActualWidth - SourceWidth * scale) / 2d;
        var offsetY = (ActualHeight - SourceHeight * scale) / 2d;

        foreach (var bird in items)
        {
            var x = offsetX + bird.Box.X * SourceWidth * scale;
            var y = offsetY + bird.Box.Y * SourceHeight * scale;
            var width = bird.Box.Width * SourceWidth * scale;
            var height = bird.Box.Height * SourceHeight * scale;

            if (width < 2 || height < 2)
            {
                continue;
            }

            var rect = new Rect(x, y, width, height);
            drawingContext.DrawRectangle(null, BoxPen, rect);

            var label = string.Create(
                CultureInfo.CurrentCulture,
                $"bird {bird.Confidence:P0}");

            var text = new FormattedText(
                label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                13,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var badge = new Rect(x, Math.Max(0, y - text.Height - 4), text.Width + 10, text.Height + 4);
            drawingContext.DrawRectangle(new SolidColorBrush(Color.FromArgb(210, 20, 120, 70)), null, badge);
            drawingContext.DrawText(text, new Point(badge.X + 5, badge.Y + 2));
        }
    }

    private static Pen CreatePen()
    {
        var brush = new SolidColorBrush(Color.FromRgb(88, 226, 148));
        brush.Freeze();
        var pen = new Pen(brush, 2);
        pen.Freeze();
        return pen;
    }
}
