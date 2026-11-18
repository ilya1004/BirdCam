using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using BirdCam.Domain.ValueObjects;
using BirdCam.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BirdCam.Infrastructure.Capture;

/// <summary>
/// Читает RTSP-поток через OpenCV (FFmpeg-бэкенд, HEVC поддерживается) и хранит
/// последний кадр. Потребители забирают копию кадра по запросу.
/// </summary>
public sealed class OpenCvFrameSource : IFrameSource, IAsyncDisposable
{
    private readonly ISettingsProvider _settings;
    private readonly ILogger<OpenCvFrameSource> _logger;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Mat? _latest;
    private Mat? _display;
    private int _width;
    private int _height;
    private double _fps;
    private DateTimeOffset? _lastFrameAt;
    private string? _lastError;
    private volatile bool _running;

    public OpenCvFrameSource(ISettingsProvider settings, ILogger<OpenCvFrameSource> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public bool IsRunning => _running;

    public bool HasFrame
    {
        get
        {
            lock (_sync)
            {
                return _latest is not null && !_latest.Empty();
            }
        }
    }

    public string? LastError => _lastError;

    public int Width
    {
        get
        {
            lock (_sync)
            {
                return _width;
            }
        }
    }

    public int Height
    {
        get
        {
            lock (_sync)
            {
                return _height;
            }
        }
    }

    public double Fps => _fps;

    public DateTimeOffset? LastFrameAt => _lastFrameAt;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_running)
            {
                return;
            }

            _running = true;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
            _logger.LogInformation("Запуск захвата RTSP");
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;

        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
            _running = false;
        }
        finally
        {
            _stateGate.Release();
        }

        if (cts is null)
        {
            return;
        }

        await cts.CancelAsync().ConfigureAwait(false);

        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ожидаемо
            }
        }

        cts.Dispose();
        _logger.LogInformation("Захват RTSP остановлен");
    }

    public bool TryGetFrame(out VideoFrame? frame, int maxWidth = 0)
    {
        frame = null;

        lock (_sync)
        {
            if (_latest is null || _latest.Empty())
            {
                return false;
            }

            var source = _latest;
            if (maxWidth > 0 && _latest.Width > maxWidth)
            {
                var scale = (double)maxWidth / _latest.Width;
                var target = new Size(maxWidth, Math.Max(1, (int)Math.Round(_latest.Height * scale)));
                _display ??= new Mat();
                Cv2.Resize(_latest, _display, target, 0, 0, InterpolationFlags.Area);
                source = _display;
            }

            frame = OpenCvFrameConverter.ToFrame(source, DateTimeOffset.UtcNow);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        lock (_sync)
        {
            _latest?.Dispose();
            _latest = null;
            _display?.Dispose();
            _display = null;
        }

        _stateGate.Dispose();
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var url = _settings.Current.Camera.RtspUrl;

        while (!cancellationToken.IsCancellationRequested)
        {
            VideoCapture? capture = null;

            try
            {
                capture = new VideoCapture(url, VideoCaptureAPIs.FFMPEG);
                capture.Set(VideoCaptureProperties.BufferSize, 1);

                if (!capture.IsOpened())
                {
                    _lastError = "Не удалось открыть RTSP-поток";
                    _logger.LogWarning("RTSP недоступен: {Url}", url);
                    await Task.Delay(3_000, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _lastError = null;
                _logger.LogInformation("RTSP подключён: {Url}", url);

                using var mat = new Mat();
                var fpsWindow = DateTime.UtcNow;
                var framesInWindow = 0;

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!capture.Read(mat) || mat.Empty())
                    {
                        _lastError = "Поток прерван";
                        _logger.LogWarning("Поток прерван, переподключение");
                        break;
                    }

                    lock (_sync)
                    {
                        _latest ??= new Mat();
                        mat.CopyTo(_latest);
                        _width = mat.Width;
                        _height = mat.Height;
                    }

                    _lastFrameAt = DateTimeOffset.UtcNow;
                    framesInWindow++;

                    var elapsed = (DateTime.UtcNow - fpsWindow).TotalSeconds;
                    if (elapsed >= 1d)
                    {
                        _fps = framesInWindow / elapsed;
                        framesInWindow = 0;
                        fpsWindow = DateTime.UtcNow;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _lastError = exception.Message;
                _logger.LogError(exception, "Ошибка захвата RTSP");
            }
            finally
            {
                capture?.Dispose();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(2_000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _running = false;
    }
}
