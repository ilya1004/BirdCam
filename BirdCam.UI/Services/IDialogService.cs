namespace BirdCam.UI.Services;

public interface IDialogService
{
    void Info(string message);

    void Error(string message);

    bool Confirm(string message);

    string? PickFolder(string? initialDirectory = null);

    string? PickFile(string filter, string? initialDirectory = null);

    string? SaveFile(string filter, string defaultFileName);

    void OpenFile(string path);

    void OpenFolder(string path);
}
