using System.IO;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace BirdCam.UI.Services;

public sealed class DialogService : IDialogService
{
    public void Info(string message) => Show(message, MessageBoxImage.Information);

    public void Error(string message) => Show(message, MessageBoxImage.Error);

    public bool Confirm(string message)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var result = owner is null
            ? MessageBox.Show(message, "BirdCam", MessageBoxButton.YesNo, MessageBoxImage.Question)
            : MessageBox.Show(owner, message, "BirdCam", MessageBoxButton.YesNo, MessageBoxImage.Question);

        return result == MessageBoxResult.Yes;
    }

    public string? PickFolder(string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Выберите папку",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл",
            Filter = filter,
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
        {
            Error("Файл не найден: " + path);
            return;
        }

        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Error("Папка не найдена: " + path);
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void Show(string message, MessageBoxImage image)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is null)
        {
            MessageBox.Show(message, "BirdCam", MessageBoxButton.OK, image);
            return;
        }

        MessageBox.Show(owner, message, "BirdCam", MessageBoxButton.OK, image);
    }
}


