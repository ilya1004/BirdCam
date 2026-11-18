namespace BirdCam.Application.Storage;

/// <summary>
/// Раскладка папок внутри корня хранилища. Пути в БД хранятся относительно этого корня.
/// </summary>
public sealed class StorageLayout
{
    public StorageLayout(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Не задан корневой путь хранилища.", nameof(rootPath));
        }

        Root = Path.GetFullPath(rootPath);
    }

    public string Root { get; }

    public string Media => Path.Combine(Root, "media");

    public string Snapshots => Path.Combine(Media, "snapshots");

    public string Clips => Path.Combine(Media, "clips");

    public string Thumbnails => Path.Combine(Media, "thumbs");

    public string Dataset => Path.Combine(Root, "dataset");

    public string DatasetImages => Path.Combine(Dataset, "images");

    public string DatasetLabels => Path.Combine(Dataset, "labels");

    public string DatasetCrops => Path.Combine(Dataset, "crops");

    public string Models => Path.Combine(Root, "models");

    public string Logs => Path.Combine(Root, "logs");

    public string Database => Path.Combine(Root, "birdcam.db");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Snapshots);
        Directory.CreateDirectory(Clips);
        Directory.CreateDirectory(Thumbnails);
        Directory.CreateDirectory(DatasetImages);
        Directory.CreateDirectory(DatasetLabels);
        Directory.CreateDirectory(DatasetCrops);
        Directory.CreateDirectory(Models);
        Directory.CreateDirectory(Logs);
    }
}
