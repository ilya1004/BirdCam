using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdCam.Application.Storage;
using BirdCam.UI.ViewModels;
using BirdCam.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BirdCam.UI.Diagnostics;

/// <summary>Оффскрин-рендер страницы в PNG (для документации и визуальных проверок).</summary>
internal static class ScreenshotRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, StorageLayout layout, string page, string outputPath)
    {
        try
        {
            FrameworkElement view;
            object viewModel;

            switch (page.ToLowerInvariant())
            {
                case "gallery":
                    view = new GalleryView();
                    viewModel = services.GetRequiredService<GalleryViewModel>();
                    break;
                case "dataset":
                    view = new DatasetView();
                    viewModel = services.GetRequiredService<DatasetViewModel>();
                    break;
                case "training":
                    view = new TrainingView();
                    viewModel = services.GetRequiredService<TrainingViewModel>();
                    break;
                case "live":
                    view = new LiveView();
                    viewModel = services.GetRequiredService<LiveViewModel>();
                    break;
                default:
                    view = new SettingsView();
                    viewModel = services.GetRequiredService<SettingsViewModel>();
                    break;
            }

            if (viewModel is TrainingViewModel training)
            {
                await training.EnsureLoadedAsync();
            }

            view.DataContext = viewModel;

            var background = System.Windows.Application.Current.Resources["BgBrush"] as Brush ?? Brushes.Black;
            var host = new Border
            {
                Background = background,
                Child = view
            };

            host.Measure(new Size(1400, 900));
            host.Arrange(new Rect(0, 0, 1400, 900));
            host.UpdateLayout();

            var bitmap = new RenderTargetBitmap(1400, 900, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = File.Create(outputPath);
            encoder.Save(stream);

            return 0;
        }
        catch (Exception exception)
        {
            Directory.CreateDirectory(layout.Logs);
            File.WriteAllText(Path.Combine(layout.Logs, "screenshot.log"), exception.ToString());
            return 1;
        }
    }
}
