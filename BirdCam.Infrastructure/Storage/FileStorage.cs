using System.Diagnostics;
using System.Globalization;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Storage;
using BirdCam.Domain.ValueObjects;
using BirdCam.Infrastructure.Imaging;
using OpenCvSharp;

namespace BirdCam.Infrastructure.Storage;

public sealed class FileStorage : IFileStorage
{
    public FileStorage(StorageLayout layout)
    {
        RootPath = layout.Root;
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetAbsolutePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Пустой относительный путь.", nameof(relativePath));
        }

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.Combine(RootPath, normalized));
    }

    public string EnsureDirectory(string relativeFolder)
    {
        var path = GetAbsolutePath(relativeFolder);
        Directory.CreateDirectory(path);
        return path;
    }

    public Task<string> SaveJpegAsync(
        VideoFrame frame,
        string relativeFolder,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectory(relativeFolder);
        using var mat = OpenCvFrameConverter.ToMat(frame);

        var absolutePath = GetAbsolutePath(Path.Combine(relativeFolder, fileName));
        Cv2.ImWrite(absolutePath, mat, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));

        return Task.FromResult(Combine(relativeFolder, fileName));
    }

    public Task<string> SaveJpegRegionAsync(
        VideoFrame frame,
        BoundingBox region,
        string relativeFolder,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectory(relativeFolder);
        using var mat = OpenCvFrameConverter.ToMat(frame);
        using var crop = new Mat(mat, ToRect(region, frame.Width, frame.Height));

        var absolutePath = GetAbsolutePath(Path.Combine(relativeFolder, fileName));
        Cv2.ImWrite(absolutePath, crop, new ImageEncodingParam(ImwriteFlags.JpegQuality, 92));

        return Task.FromResult(Combine(relativeFolder, fileName));
    }

    public async Task<string> SaveTextAsync(
        string relativeFolder,
        string fileName,
        string content,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectory(relativeFolder);
        var absolutePath = GetAbsolutePath(Path.Combine(relativeFolder, fileName));
        await File.WriteAllTextAsync(absolutePath, content, cancellationToken).ConfigureAwait(false);
        return Combine(relativeFolder, fileName);
    }

    public Task<string> CreateThumbnailAsync(
        string sourceRelativePath,
        string targetRelativeFolder,
        string fileName,
        int maxWidth = 320,
        CancellationToken cancellationToken = default)
    {
        EnsureDirectory(targetRelativeFolder);

        using var image = LoadImage(GetAbsolutePath(sourceRelativePath));
        if (image.Empty())
        {
            throw new InvalidOperationException($"Не удалось прочитать изображение {sourceRelativePath}");
        }

        var scale = Math.Min(1d, (double)Math.Max(1, maxWidth) / image.Width);
        using var resized = scale < 1d
            ? image.Resize(new Size(Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale))))
            : image.Clone();

        var absolutePath = GetAbsolutePath(Path.Combine(targetRelativeFolder, fileName));
        Cv2.ImWrite(absolutePath, resized, new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));

        return Task.FromResult(Combine(targetRelativeFolder, fileName));
    }

    public bool Exists(string relativePath) => File.Exists(GetAbsolutePath(relativePath));

    public long GetFileSize(string relativePath)
    {
        var info = new FileInfo(GetAbsolutePath(relativePath));
        return info.Exists ? info.Length : 0;
    }

    public void Delete(string relativePath)
    {
        var path = GetAbsolutePath(relativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string Combine(string folder, string fileName)
        => $"{folder.TrimEnd('/', '\\')}/{fileName}";

    private static Rect ToRect(BoundingBox region, int width, int height)
    {
        var box = region.Clamp();
        var (x, y, w, h) = box.ToPixels(width, height);

        var left = Math.Clamp((int)Math.Round(x), 0, width - 1);
        var top = Math.Clamp((int)Math.Round(y), 0, height - 1);
        var right = Math.Clamp((int)Math.Round(x + w), left + 1, width);
        var bottom = Math.Clamp((int)Math.Round(y + h), top + 1, height);

        return new Rect(left, top, right - left, bottom - top);
    }

    private static Mat LoadImage(string absolutePath)
    {
        var image = Cv2.ImRead(absolutePath, ImreadModes.Color);
        if (!image.Empty())
        {
            return image;
        }

        image.Dispose();

        using var capture = new VideoCapture(absolutePath);
        var frame = new Mat();
        if (capture.IsOpened() && capture.Read(frame) && !frame.Empty())
        {
            return frame;
        }

        frame.Dispose();
        return new Mat();
    }
}
