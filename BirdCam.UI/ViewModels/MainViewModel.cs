using System.Collections.ObjectModel;
using System.Reflection;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BirdCam.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly GalleryViewModel _gallery;
    private readonly DatasetViewModel _dataset;

    public MainViewModel(
        LiveViewModel live,
        GalleryViewModel gallery,
        DatasetViewModel dataset,
        SettingsViewModel settings,
        StatusService status)
    {
        Live = live;
        _gallery = gallery;
        _dataset = dataset;
        Settings = settings;
        Status = status;

        NavigationItems =
        [
            new NavigationItem("📹", "Прямой эфир", "live"),
            new NavigationItem("🖼️", "Галерея", "gallery"),
            new NavigationItem("📊", "Датасет", "dataset"),
            new NavigationItem("⚙️", "Настройки", "settings")
        ];

        VersionText = "BirdCam v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

        Live.MediaAdded += (_, _) => _ = _gallery.RefreshAsync();
        Live.DatasetAdded += (_, _) => _ = _dataset.RefreshAsync();

        SelectedNavigation = NavigationItems[0];

        _ = _gallery.RefreshAsync();
        _ = _dataset.RefreshAsync();
    }

    public LiveViewModel Live { get; }

    public GalleryViewModel Gallery => _gallery;

    public DatasetViewModel Dataset => _dataset;

    public SettingsViewModel Settings { get; }

    public StatusService Status { get; }

    public string VersionText { get; }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    [ObservableProperty]
    private NavigationItem? _selectedNavigation;

    [ObservableProperty]
    private object? _currentPage;

    public void NavigateTo(string key)
    {
        var item = NavigationItems.FirstOrDefault(
            navigation => string.Equals(navigation.Key, key, StringComparison.OrdinalIgnoreCase));

        if (item is not null)
        {
            SelectedNavigation = item;
        }
    }

    partial void OnSelectedNavigationChanged(NavigationItem? value)
    {
        CurrentPage = value?.Key switch
        {
            "gallery" => _gallery,
            "dataset" => _dataset,
            "settings" => Settings,
            _ => Live
        };

        switch (value?.Key)
        {
            case "gallery":
                _ = _gallery.RefreshAsync();
                break;
            case "dataset":
                _ = _dataset.RefreshAsync();
                break;
        }
    }
}

