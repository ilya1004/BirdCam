using BirdCam.Application.Settings;

namespace BirdCam.Infrastructure.Recording;

/// <summary>Ищет ffmpeg: настройка → PATH → winget → типовые пути.</summary>
public sealed class FfmpegLocator
{
    private readonly ISettingsProvider _settings;
    private string? _cached;

    public FfmpegLocator(ISettingsProvider settings)
    {
        _settings = settings;
    }

    public string? Find()
    {
        if (_cached is not null && File.Exists(_cached))
        {
            return _cached;
        }

        var configured = _settings.Current.Recording.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return _cached = configured;
        }

        var fromPath = FindOnPath("ffmpeg.exe");
        if (fromPath is not null)
        {
            return _cached = fromPath;
        }

        var wingetRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");

        if (Directory.Exists(wingetRoot))
        {
            try
            {
                var found = Directory
                    .EnumerateFiles(wingetRoot, "ffmpeg.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                {
                    return _cached = found;
                }
            }
            catch (Exception)
            {
                // нет доступа к части папок — не критично
            }
        }

        foreach (var candidate in new[]
                 {
                     @"C:\ffmpeg\bin\ffmpeg.exe",
                     @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "ffmpeg.exe")
                 })
        {
            if (File.Exists(candidate))
            {
                return _cached = candidate;
            }
        }

        return null;
    }

    public string FindRequired()
        => Find() ?? throw new InvalidOperationException(
            "ffmpeg не найден. Укажите путь к ffmpeg.exe в настройках или добавьте его в PATH.");

    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // некорректная запись PATH
            }
        }

        return null;
    }
}
