using System.Runtime.InteropServices;
using BirdCam.Domain.ValueObjects;
using OpenCvSharp;

namespace BirdCam.Infrastructure.Imaging;

/// <summary>Преобразования между кадром приложения и Mat OpenCV.</summary>
internal static class OpenCvFrameConverter
{
    public static Mat ToMat(VideoFrame frame)
    {
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        Marshal.Copy(frame.Pixels, 0, mat.Data, frame.Length);
        return mat;
    }

    /// <summary>Копирует Mat в арендованный кадр (BGR24).</summary>
    public static VideoFrame ToFrame(Mat mat, DateTimeOffset timestamp)
    {
        if (mat.Empty())
        {
            throw new ArgumentException("Пустой кадр.", nameof(mat));
        }

        var frame = VideoFrame.Rent(mat.Width, mat.Height, timestamp);
        Marshal.Copy(mat.Data, frame.Pixels, 0, mat.Width * mat.Height * 3);
        return frame;
    }
}
