using System.Text.Json;
using BirdCam.Application.Settings;

namespace BirdCam.Infrastructure.Settings;

public sealed class JsonSettingsProvider : ISettingsProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current;

    public JsonSettingsProvider(string? filePath = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BirdCam");
        Directory.CreateDirectory(directory);

        _filePath = filePath ?? Path.Combine(directory, "settings.json");
        _current = Load();
    }

    public AppSettings Current => _current;

    public event EventHandler<AppSettings>? Changed;

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
            _current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, _current);
    }

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                var defaults = new AppSettings();
                File.WriteAllText(_filePath, JsonSerializer.Serialize(defaults, JsonOptions));
                return defaults;
            }

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }
}
