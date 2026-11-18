using System.Diagnostics;
using System.Globalization;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Settings;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System.Text.Json;

namespace BirdCam.Infrastructure.Recording;

public sealed class FfmpegClipRecorder : IClipRecorder
{
    private readonly ISettingsProvider _settings;
    private readonly FfmpegLocator _locator;
    private readonly ILogger<FfmpegClipRecorder> _logger;

    public FfmpegClipRecorder(
        ISettingsProvider settings,
        FfmpegLocator locator,
        ILogger<FfmpegClipRecorder> logger)
    {
        _settings = settings;
        _locator = locator;
        _logger = logger;
    }

    public async Task<ClipRecording> RecordAsync(
        string absoluteOutputPath,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var ffmpeg = _locator.FindRequired();
        var settings = _settings.Current;

        var directory = Path.GetDirectoryName(absoluteOutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var seconds = Math.Max(1, (int)Math.Round(duration.TotalSeconds));

        var arguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-rtsp_transport", "tcp",
            "-i", settings.Camera.RtspUrl,
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-map", "0:v:0",
            "-map", "0:a?",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-crf", "23",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-b:a", "96k",
            "-movflags", "+faststart",
            absoluteOutputPath
        };

        _logger.LogInformation("Запись клипа {Seconds} с → {Path}", seconds, absoluteOutputPath);

        var exitCode = await RunAsync(ffmpeg, arguments, TimeSpan.FromSeconds(seconds + 30), cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0 || !File.Exists(absoluteOutputPath))
        {
            throw new InvalidOperationException($"ffmpeg завершился с кодом {exitCode}. Проверьте RTSP-поток.");
        }

        var (width, height, durationSeconds) = await ProbeAsync(ffmpeg, absoluteOutputPath, seconds, cancellationToken).ConfigureAwait(false);
        var size = new FileInfo(absoluteOutputPath).Length;

        return new ClipRecording(absoluteOutputPath, durationSeconds, width, height, size);
    }

    private async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // процесс уже завершился
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("ffmpeg не успел завершить запись за отведённое время.");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(stderr))
        {
            _logger.LogDebug("ffmpeg stderr: {Error}", stderr.Trim());
        }

        return process.ExitCode;
    }

    private static async Task<(int Width, int Height, double DurationSeconds)> ProbeAsync(
        string ffmpegPath,
        string filePath,
        int fallbackSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.GetDirectoryName(ffmpegPath);
            var ffprobe = string.IsNullOrWhiteSpace(directory) ? null : Path.Combine(directory, "ffprobe.exe");
            if (ffprobe is null || !File.Exists(ffprobe))
            {
                return (0, 0, fallbackSeconds);
            }

            var startInfo = new ProcessStartInfo(ffprobe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-v");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-select_streams");
            startInfo.ArgumentList.Add("v:0");
            startInfo.ArgumentList.Add("-show_entries");
            startInfo.ArgumentList.Add("stream=width,height:format=duration");
            startInfo.ArgumentList.Add("-of");
            startInfo.ArgumentList.Add("json");
            startInfo.ArgumentList.Add(filePath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (0, 0, fallbackSeconds);
            }

            var json = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var stream = root.GetProperty("streams")[0];
            var width = stream.GetProperty("width").GetInt32();
            var height = stream.GetProperty("height").GetInt32();

            var duration = (double)fallbackSeconds;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationElement) &&
                double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            return (width, height, Math.Round(duration, 1));
        }
        catch (Exception)
        {
            return (0, 0, fallbackSeconds);
        }
    }
}

