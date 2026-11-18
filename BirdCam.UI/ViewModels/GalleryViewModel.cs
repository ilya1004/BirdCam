using System.Collections.ObjectModel;
using System.IO;
using BirdCam.Application.Abstractions;
using BirdCam.Application.Services;
using BirdCam.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace BirdCam.UI.ViewModels;

public sealed partial class GalleryViewModel : ObservableObject
{
    private readonly MediaLibraryService _library;
    private readonly IFileStorage _storage;
    private readonly ITelegramNotifier _telegram;
    private readonly IDialogService _dialogs;
    private readonly StatusService _status;
    private readonly ILogger<GalleryViewModel> _logger;

    public GalleryViewModel(
        MediaLibraryService library,
        IFileStorage storage,
        ITelegramNotifier telegram,
        IDialogService dialogs,
        StatusService status,
        ILogger<GalleryViewModel> logger)
    {
        _library = library;
        _storage = storage;
        _telegram = telegram;
        _dialogs = dialogs;
        _status = status;
        _logger = logger;
    }

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];

    [ObservableProperty]
    private MediaCardViewModel? _selected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _summary = "Пока пусто";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var items = await _library.GetRecentAsync(500);

            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(MediaCardViewModel.From(item, _storage));
            }

            Summary = Items.Count == 0
                ? "Пока пусто — сделайте первый снимок"
                : $"{Items.Count} файлов · папка {_storage.GetAbsolutePath("media")}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось загрузить галерею");
            _status.Error("Галерея: " + exception.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SendAsync(MediaCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        try
        {
            var caption = $"BirdCam · {card.Title} · {card.Subtitle}";
            var result = card.IsClip
                ? await _telegram.SendVideoAsync(card.AbsolutePath, caption)
                : await _telegram.SendPhotoAsync(card.AbsolutePath, caption);

            if (result.Success)
            {
                await _library.MarkSentAsync(card.Id);
                _status.Set($"Отправлено в Telegram: {Path.GetFileName(card.AbsolutePath)}");
                await RefreshAsync();
            }
            else
            {
                _status.Error("Telegram: " + result.Error);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Ошибка отправки");
            _status.Error(exception.Message);
        }
    }

    [RelayCommand]
    private void Open(MediaCardViewModel? card)
    {
        if (card is not null)
        {
            _dialogs.OpenFile(card.AbsolutePath);
        }
    }

    [RelayCommand]
    private void ShowInFolder(MediaCardViewModel? card)
    {
        if (card is not null)
        {
            _dialogs.OpenFolder(Path.GetDirectoryName(card.AbsolutePath) ?? _storage.RootPath);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(MediaCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        if (!_dialogs.Confirm($"Удалить файл?\n{Path.GetFileName(card.AbsolutePath)}"))
        {
            return;
        }

        try
        {
            await _library.DeleteAsync(card.Id);
            _status.Set("Файл удалён");
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Не удалось удалить файл");
            _status.Error(exception.Message);
        }
    }

    [RelayCommand]
    private void OpenMediaFolder() => _dialogs.OpenFolder(_storage.GetAbsolutePath("media/snapshots"));
}
