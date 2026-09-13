using System.IO;

namespace WeArchive.App.Services;

/// <summary>Folder selection, abstracted so the view model stays testable.</summary>
public interface IFolderPicker
{
    string? PickFolder(string? initialDirectory, string title);
}

/// <summary>WPF implementation backed by the platform folder dialog.</summary>
public sealed class FolderPicker : IFolderPicker
{
    public string? PickFolder(string? initialDirectory, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
