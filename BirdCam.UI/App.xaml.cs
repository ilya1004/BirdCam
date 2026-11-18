using System.IO;
using System.Windows;
using BirdCam.Application;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Application.Storage;
using BirdCam.Infrastructure;
using BirdCam.Infrastructure.Logging;
using BirdCam.Infrastructure.Persistence;
using BirdCam.Infrastructure.Settings;
using BirdCam.UI.Services;
using BirdCam.UI.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsProvider = new JsonSettingsProvider();
        var layout = new StorageLayout(settingsProvider.Current.Storage.RootPath);
        layout.EnsureCreated();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddProvider(new FileLoggerProvider(layout.Logs));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Warning);

        builder.Services.AddSingleton<ISettingsProvider>(settingsProvider);
        builder.Services.AddSingleton(layout);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddBirdCamApplication();
        builder.Services.AddBirdCamInfrastructure(layout);

        builder.Services.AddSingleton<StatusService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddSingleton<LiveViewModel>();
        builder.Services.AddSingleton<GalleryViewModel>();
        builder.Services.AddSingleton<DatasetViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        await _host.StartAsync();

        try
        {
            await InitializeDatabaseAsync(_host.Services);

            var detector = _host.Services.GetRequiredService<IDetector>();
            await detector.InitializeAsync();

            if (e.Args.Contains("--ui-smoke", StringComparer.OrdinalIgnoreCase))
            {
                var uiCode = Diagnostics.UiSmokeRunner.Run(_host.Services, layout);
                Shutdown(uiCode);
                return;
            }

            if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
            {
                var exitCode = await Diagnostics.SmokeRunner.RunAsync(_host.Services, layout, e.Args);
                Shutdown(exitCode);
                return;
            }

            var settings = settingsProvider.Current;
            var frames = _host.Services.GetRequiredService<IFrameSource>();

            if (settings.Camera.AutoStart)
            {
                await frames.StartAsync();

                if (settings.Detection.AutoStart)
                {
                    await _host.Services.GetRequiredService<IBirdWatcher>().StartAsync();
                }
            }

            var window = _host.Services.GetRequiredService<MainWindow>();
            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();

            var startPage = GetOption(e.Args, "--page");
            if (!string.IsNullOrWhiteSpace(startPage))
            {
                mainViewModel.NavigateTo(startPage);
            }

            window.DataContext = mainViewModel;
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Не удалось запустить BirdCam:\n" + exception,
                "BirdCam",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try
            {
                var frames = _host.Services.GetService<IFrameSource>();
                if (frames is IAsyncDisposable framesDisposable)
                {
                    framesDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                var watcher = _host.Services.GetService<IBirdWatcher>();
                if (watcher is not null)
                {
                    watcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

                _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                _host.Dispose();
            }
            catch (Exception)
            {
                // принудительное завершение — не мешаем выходу
            }
        }

        base.OnExit(e);
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<BirdCamDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}



