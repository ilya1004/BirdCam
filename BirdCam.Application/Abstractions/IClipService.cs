using BirdCam.Domain.Entities;

namespace BirdCam.Application.Abstractions;

public interface IClipService
{
    Task<MediaItem> RecordAsync(TimeSpan duration, CancellationToken cancellationToken = default);
}
