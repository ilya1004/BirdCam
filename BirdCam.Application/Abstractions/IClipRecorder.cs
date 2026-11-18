namespace BirdCam.Application.Abstractions;

public sealed record ClipRecording(string FilePath, double DurationSeconds, int Width, int Height, long SizeBytes);

public interface IClipRecorder
{
    /// <summary>Записывает клип с RTSP-потока в MP4 (H.264) по указанному абсолютному пути.</summary>
    Task<ClipRecording> RecordAsync(string absoluteOutputPath, TimeSpan duration, CancellationToken cancellationToken = default);
}
