using BirdCam.Application.Abstractions;
using BirdCam.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BirdCam.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddBirdCamApplication(this IServiceCollection services)
    {
        services.AddSingleton<ISnapshotService, SnapshotService>();
        services.AddSingleton<IDatasetService, DatasetService>();
        services.AddSingleton<IClipService, ClipService>();
        services.AddSingleton<MediaLibraryService>();
        services.AddSingleton<IBirdWatcher, BirdWatcher>();

        return services;
    }
}
