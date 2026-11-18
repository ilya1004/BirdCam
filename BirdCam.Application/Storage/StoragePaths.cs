namespace BirdCam.Application.Storage;

/// <summary>Относительные папки, которые используются в БД и файловом хранилище.</summary>
public static class StoragePaths
{
    public const string Snapshots = "media/snapshots";

    public const string Clips = "media/clips";

    public const string Thumbnails = "media/thumbs";

    public const string Dataset = "dataset";

    public const string DatasetImages = "dataset/images";

    public const string DatasetLabels = "dataset/labels";

    public const string DatasetCrops = "dataset/crops";

    public const string Models = "models";
}
