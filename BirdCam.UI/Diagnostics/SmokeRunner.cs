using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Domain.Enums;
using BirdCam.Domain.ValueObjects;
using BirdCam.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BirdCam.UI.Diagnostics;

/// <summary>
/// Проверка сквозного сценария без UI.
/// --smoke: кадр → детекция → снимок → датасет → клип.
/// --smoke --image path.jpg: детекция на конкретной картинке.
/// Результат пишется в logs/smoke.log.
/// </summary>
internal static class SmokeRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, StorageLayout layout, string[] args)
    {
        var log = new StringBuilder();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("smoke");

        void Write(string line)
        {
            log.AppendLine($"{DateTime.Now:HH:mm:ss} {line}");
            logger.LogInformation("smoke: {Line}", line);
        }

        try
        {
            Write($"storage: {layout.Root}");

            var dbFactory = services.GetRequiredService<IDbContextFactory<BirdCamDbContext>>();
            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                await db.Database.EnsureCreatedAsync();
            }

            Write("db: ok");

            var detector = services.GetRequiredService<IDetector>();
            await detector.InitializeAsync();
            Write($"detector: {detector.Status}");

            var imagePath = GetOption(args, "--image");
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                return await RunImageAsync(imagePath, detector, Write);
            }

            var frames = services.GetRequiredService<IFrameSource>();
            await frames.StartAsync();

            VideoFrame? frame = null;
            for (var attempt = 0; attempt < 40 && frame is null; attempt++)
            {
                await Task.Delay(500);
                if (frames.TryGetFrame(out var candidate))
                {
                    frame = candidate;
                }
            }

            if (frame is null)
            {
                Write($"frame: FAIL ({frames.LastError ?? "нет кадра"})");
                Write("RESULT: FAIL");
                return 2;
            }

            using (frame)
            {
                Write($"frame: {frame.Width}x{frame.Height}");

                var birds = detector.IsAvailable
                    ? await detector.DetectAsync(frame)
                    : Array.Empty<DetectedBird>();

                var best = birds.Count > 0 ? birds.Max(bird => bird.Confidence) : 0d;
                Write($"detect: {birds.Count} bird(s), best={best:P1}");

                var snapshot = await services.GetRequiredService<ISnapshotService>()
                    .SaveFrameAsync(frame, "smoke");
                Write($"snapshot: {snapshot.FilePath}");

                var sample = await services.GetRequiredService<IDatasetService>()
                    .AddAsync(frame, birds, DatasetSource.Manual);
                Write($"dataset: {sample.ImagePath}, crop={sample.CropPath ?? "-"}");
            }

            var clip = await services.GetRequiredService<IClipService>()
                .RecordAsync(TimeSpan.FromSeconds(4));
            Write($"clip: {clip.FilePath} ({clip.DurationSeconds:0.#} s, {clip.Width}x{clip.Height}, {clip.SizeBytes / 1024} KB)");

            await frames.StopAsync();
            Write("RESULT: OK");
            return 0;
        }
        catch (Exception exception)
        {
            Write("RESULT: FAIL " + exception);
            return 1;
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(layout.Logs);
                await File.WriteAllTextAsync(Path.Combine(layout.Logs, "smoke.log"), log.ToString());
            }
            catch (Exception)
            {
                // нечего делать
            }
        }
    }

    private static async Task<int> RunImageAsync(
        string imagePath,
        IDetector detector,
        Action<string> write)
    {
        using var mat = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (mat.Empty())
        {
            write($"image: FAIL, не удалось прочитать {imagePath}");
            write("RESULT: FAIL");
            return 3;
        }

        var frame = VideoFrame.Rent(mat.Width, mat.Height, DateTimeOffset.UtcNow);
        try
        {
            Marshal.Copy(mat.Data, frame.Pixels, 0, mat.Width * mat.Height * 3);

            var birds = await detector.DetectAsync(frame);
            write($"image: {mat.Width}x{mat.Height}, birds={birds.Count}");

            foreach (var bird in birds.Take(10))
            {
                write($"  {bird.Label} {bird.Confidence:P1} box=({bird.Box.X:0.000}, {bird.Box.Y:0.000}, {bird.Box.Width:0.000}, {bird.Box.Height:0.000})");
            }

            for (var warmup = 0; warmup < 3; warmup++)
            {
                _ = await detector.DetectAsync(frame);
            }

            const int runs = 10;
            var stopwatch = Stopwatch.StartNew();
            for (var run = 0; run < runs; run++)
            {
                _ = await detector.DetectAsync(frame);
            }

            stopwatch.Stop();
            write($"detect avg: {stopwatch.Elapsed.TotalMilliseconds / runs:0.0} ms ({detector.InputSize}px)");

            write("RESULT: OK");
            return 0;
        }
        finally
        {
            frame.Dispose();
        }
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
}

