using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Infrastructure.Capture;
using BirdCam.Infrastructure.Detection;
using BirdCam.Infrastructure.Go2Rtc;
using BirdCam.Infrastructure.Persistence;
using BirdCam.Infrastructure.Persistence.Repositories;
using BirdCam.Infrastructure.Recording;
using BirdCam.Infrastructure.Storage;
using BirdCam.Infrastructure.Telegram;
using BirdCam.Infrastructure.Training;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BirdCam.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBirdCamInfrastructure(this IServiceCollection services, StorageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(layout);

        services.AddSingleton(layout);
        services.AddSingleton<IFileStorage, FileStorage>();

        services.AddDbContextFactory<BirdCamDbContext>(
            options => options.UseSqlite($"Data Source={layout.Database}"));

        services.AddSingleton<IMediaRepository, MediaRepository>();
        services.AddSingleton<IDatasetRepository, DatasetRepository>();
        services.AddSingleton<IDetectionRepository, DetectionRepository>();

        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<IClipRecorder, FfmpegClipRecorder>();
        services.AddSingleton<ITelegramNotifier, TelegramBotNotifier>();

        services.AddSingleton<IFrameSource, OpenCvFrameSource>();
        services.AddSingleton<IDetector, OnnxBirdDetector>();
        services.AddSingleton<IGo2RtcManager, Go2RtcManager>();
        services.AddSingleton<ITrainingDataService, TrainingDataService>();
        services.AddSingleton<ITrainingRunner, UltralyticsTrainingRunner>();

        return services;
    }
}
