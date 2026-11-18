using BirdCam.Domain.ValueObjects;

namespace BirdCam.Application.Abstractions;

/// <summary>Файловое хранилище: картинки, разметка, превью. Все пути — относительно корня.</summary>
public interface IFileStorage
{
    string RootPath { get; }

    string GetAbsolutePath(string relativePath);

    string EnsureDirectory(string relativeFolder);

    Task<string> SaveJpegAsync(VideoFrame frame, string relativeFolder, string fileName, CancellationToken cancellationToken = default);

    Task<string> SaveJpegRegionAsync(
        VideoFrame frame,
        BoundingBox region,
        string relativeFolder,
        string fileName,
        CancellationToken cancellationToken = default);

    Task<string> SaveTextAsync(string relativeFolder, string fileName, string content, CancellationToken cancellationToken = default);

    Task<string> CreateThumbnailAsync(
        string sourceRelativePath,
        string targetRelativeFolder,
        string fileName,
        int maxWidth = 320,
        CancellationToken cancellationToken = default);

    bool Exists(string relativePath);

    long GetFileSize(string relativePath);

    void Delete(string relativePath);
}
