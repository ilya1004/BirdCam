using System.Diagnostics;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using Microsoft.Extensions.Logging;

namespace BirdCam.Infrastructure.Go2Rtc;

/// <summary>
/// Проверяет доступность локального go2rtc и при необходимости запускает его.
/// Останавливает только тот процесс, который запустило само приложение.
/// </summary>
public sealed class Go2RtcManager : IGo2RtcManager, IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    private readonly ISettingsProvider _settings;
    private readonly ILogger<Go2RtcManager> _logger;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();

    private Process? _process;
    private bool _startedByApp;

    public Go2RtcManager(ISettingsProvider settings, ILogger<Go2RtcManager> logger)
    {
        _settings = settings;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    }

    public bool IsManagedProcessRunning
    {
        get
        {
            lock (_sync)
            {
                return _startedByApp && _process is { HasExited: false };
            }
        }
    }

    public async Task<Go2RtcStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        DropExitedProcess();

        var api = GetApiBaseUrl();
        try
        {
            using var response = await _http.GetAsync(api + "/api", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Go2RtcStatus.Unavailable($"go2rtc отвечает HTTP {(int)response.StatusCode}");
            }

            return new Go2RtcStatus(
                true,
                _startedByApp,
                _startedByApp ? "go2rtc запущен приложением" : "go2rtc доступен");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Go2RtcStatus.Unavailable($"go2rtc не отвечает: {api}");
        }
        catch (HttpRequestException exception)
        {
            return Go2RtcStatus.Unavailable($"go2rtc недоступен ({api}): {exception.Message}");
        }
    }

    public async Task<Go2RtcStatus> EnsureAvailableAsync(CancellationToken cancellationToken = default)
    {
        var status = await CheckAsync(cancellationToken).ConfigureAwait(false);
        if (status.Available)
        {
            return status;
        }

        var camera = _settings.Current.Camera;
        if (!camera.ManageGo2Rtc)
        {
            return Go2RtcStatus.Unavailable("go2rtc не запущен, автозапуск отключён в настройках");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            status = await CheckAsync(cancellationToken).ConfigureAwait(false);
            if (status.Available)
            {
                return status;
            }

            var executable = ResolveExecutable(camera.Go2RtcPath);
            if (executable is null)
            {
                var message = string.IsNullOrWhiteSpace(camera.Go2RtcPath)
                    ? "go2rtc.exe не найден. Укажите путь к нему в настройках или добавьте в PATH."
                    : $"go2rtc.exe не найден по указанному пути: {camera.Go2RtcPath}";
                return Go2RtcStatus.Unavailable(message);
            }

            try
            {
                var startInfo = new ProcessStartInfo(executable)
                {
                    WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var process = Process.Start(startInfo);
                if (process is null)
                {
                    return Go2RtcStatus.Unavailable("не удалось запустить go2rtc.exe");
                }

                lock (_sync)
                {
                    _process = process;
                    _startedByApp = true;
                }

                _logger.LogInformation("Запускаю go2rtc: {Path}", executable);

                var deadline = DateTime.UtcNow + StartTimeout;
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(400, cancellationToken).ConfigureAwait(false);

                    if (process.HasExited)
                    {
                        var exitCode = process.ExitCode;
                        ResetManagedProcess();
                        process.Dispose();
                        return Go2RtcStatus.Unavailable($"go2rtc завершился при запуске (код {exitCode})");
                    }

                    status = await CheckAsync(cancellationToken).ConfigureAwait(false);
                    if (status.Available)
                    {
                        _logger.LogInformation("go2rtc запущен и отвечает по API");
                        return new Go2RtcStatus(true, true, "go2rtc запущен приложением");
                    }
                }

                KillProcess(process);
                ResetManagedProcess();
                return Go2RtcStatus.Unavailable($"go2rtc не ответил за {StartTimeout.TotalSeconds:0} секунд");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Не удалось запустить go2rtc");
                ResetManagedProcess();
                return Go2RtcStatus.Unavailable("ошибка запуска go2rtc: " + exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopManagedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process? process;
            lock (_sync)
            {
                process = _process;
                _process = null;
                _startedByApp = false;
            }

            if (process is null)
            {
                return;
            }

            _logger.LogInformation("Останавливаю go2rtc, запущенный приложением");

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                using var content = new StringContent(string.Empty);
                using var response = await _http
                    .PostAsync(GetApiBaseUrl() + "/api/exit?code=0", content, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "go2rtc не завершился через API, завершаю процесс");
            }

            try
            {
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Не удалось корректно завершить go2rtc");
            }
            finally
            {
                process.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
            _startedByApp = false;
        }

        if (process is not null)
        {
            KillProcess(process);
        }

        _http.Dispose();
        _gate.Dispose();
    }

    private void DropExitedProcess()
    {
        Process? process = null;

        lock (_sync)
        {
            if (_startedByApp && _process is { HasExited: true })
            {
                process = _process;
                _process = null;
                _startedByApp = false;
            }
        }

        if (process is null)
        {
            return;
        }

        _logger.LogWarning("go2rtc завершился с кодом {Code}", process.ExitCode);
        process.Dispose();
    }

    private void ResetManagedProcess()
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _process = null;
            _startedByApp = false;
        }

        process?.Dispose();
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (Exception)
        {
            // процесс уже завершился
        }
        finally
        {
            process.Dispose();
        }
    }

    private string GetApiBaseUrl()
    {
        var configured = _settings.Current.Camera.Go2RtcApiUrl;
        var url = string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:1984" : configured.Trim();
        return url.TrimEnd('/');
    }

    private static string? ResolveExecutable(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            return File.Exists(expanded) ? expanded : null;
        }

        var fromPath = FindOnPath("go2rtc.exe");
        if (fromPath is not null)
        {
            return fromPath;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach (var candidate in new[]
                 {
                     Path.Combine(userProfile, "Projects", "go2rtc_win64", "go2rtc.exe"),
                     Path.Combine(userProfile, "Projects", "go2rtc", "go2rtc.exe"),
                     Path.Combine(localAppData, "go2rtc", "go2rtc.exe"),
                     @"C:\go2rtc\go2rtc.exe"
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var projects = Path.Combine(userProfile, "Projects");
        if (!Directory.Exists(projects))
        {
            return null;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(projects))
            {
                var direct = Path.Combine(directory, "go2rtc.exe");
                if (File.Exists(direct))
                {
                    return direct;
                }

                foreach (var nested in Directory.EnumerateDirectories(directory))
                {
                    var candidate = Path.Combine(nested, "go2rtc.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch (Exception)
        {
            // нет доступа к части папок — не критично
        }

        return null;
    }

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
