using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdCam.Domain.ValueObjects;
using BirdCam.UI.Controls;
using BirdCam.Application.Storage;
using BirdCam.UI.Services;
using BirdCam.UI.ViewModels;
using BirdCam.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BirdCam.UI.Diagnostics;

/// <summary>Проверяет, что все страницы и словари ресурсов загружаются без ошибок XAML.</summary>
internal static class UiSmokeRunner
{
    public static int Run(IServiceProvider services, StorageLayout layout)
    {
        try
        {
            _ = new MainWindow { DataContext = services.GetRequiredService<MainViewModel>() };

            var pages = new (FrameworkElement View, object ViewModel)[]
            {
                (new LiveView(), services.GetRequiredService<LiveViewModel>()),
                (new GalleryView(), services.GetRequiredService<GalleryViewModel>()),
                (new DatasetView(), services.GetRequiredService<DatasetViewModel>()),
                (new SettingsView(), services.GetRequiredService<SettingsViewModel>())
            };

            foreach (var (view, viewModel) in pages)
            {
                view.DataContext = viewModel;
                view.Measure(new Size(1440, 900));
                view.Arrange(new Rect(0, 0, 1440, 900));
                view.UpdateLayout();
            }

            var overlay = new DetectionOverlay
            {
                SourceWidth = 1920,
                SourceHeight = 1080,
                Items = new[] { new DetectedBird("bird", 0.9, new BoundingBox(0.4, 0.4, 0.2, 0.2)) }
            };

            overlay.Measure(new Size(960, 540));
            overlay.Arrange(new Rect(0, 0, 960, 540));
            overlay.UpdateLayout();

            var bitmap = new RenderTargetBitmap(960, 540, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(overlay);

            var pixels = new byte[960 * 540 * 4];
            bitmap.CopyPixels(pixels, 960 * 4, 0);

            var hasDrawing = false;
            for (var index = 3; index < pixels.Length; index += 4)
            {
                if (pixels[index] > 0)
                {
                    hasDrawing = true;
                    break;
                }
            }

            if (!hasDrawing)
            {
                throw new InvalidOperationException("DetectionOverlay ничего не нарисовал.");
            }

            File.WriteAllText(Path.Combine(layout.Logs, "ui-smoke.log"), "OK");
            return 0;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(layout.Logs);
            File.WriteAllText(Path.Combine(layout.Logs, "ui-smoke.log"), exception.ToString());
            return 1;
        }
    }
}

