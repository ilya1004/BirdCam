using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BirdCam.UI.Services;

/// <summary>Строка состояния внизу окна. Обновления можно присылать из любого потока.</summary>
public sealed partial class StatusService : ObservableObject
{
    [ObservableProperty]
    private string _message = "Готово";

    public void Set(string message) => RunOnUi(() => Message = message);

    public void Error(string message) => RunOnUi(() => Message = "⚠ " + message);

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}

