using System.Buffers;

namespace BirdCam.Domain.ValueObjects;

/// <summary>
/// Кадр в формате BGR24 (как его отдаёт OpenCV). Буфер арендуется из пула,
/// поэтому кадр нужно освобождать после использования.
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? _pixels;
    private readonly bool _pooled;

    public VideoFrame(byte[] pixels, int width, int height, DateTimeOffset timestamp)
        : this(pixels, width * height * 3, width, height, timestamp, pooled: false)
    {
    }

    private VideoFrame(byte[] pixels, int length, int width, int height, DateTimeOffset timestamp, bool pooled)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Размеры кадра должны быть положительными.");
        }

        if (pixels.Length < length)
        {
            throw new ArgumentException("Буфер меньше заявленного размера кадра.", nameof(pixels));
        }

        _pixels = pixels;
        Length = length;
        Width = width;
        Height = height;
        Timestamp = timestamp;
        _pooled = pooled;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Количество значимых байт в буфере.</summary>
    public int Length { get; }

    public int Stride => Width * 3;

    public DateTimeOffset Timestamp { get; }

    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public ReadOnlySpan<byte> Span => Pixels.AsSpan(0, Length);

    public static VideoFrame Rent(int width, int height, DateTimeOffset timestamp)
    {
        var length = checked(width * height * 3);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        return new VideoFrame(buffer, length, width, height, timestamp, pooled: true);
    }

    public void Dispose()
    {
        var buffer = _pixels;
        _pixels = null;

        if (_pooled && buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
