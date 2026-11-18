namespace BirdCam.Application.Settings;

public interface ISettingsProvider
{
    AppSettings Current { get; }

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    event EventHandler<AppSettings>? Changed;
}
