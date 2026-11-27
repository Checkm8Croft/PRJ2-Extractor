using System.IO;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace PRJ2_Extractor.Services;

/// <summary>The questions the view model has to ask the user; the window answers them with the system's own dialogs.</summary>
public interface IDialogService
{
    /// <summary>Asks for a level file; null when the user cancels.</summary>
    Task<string?> PickLevelAsync(string? startFolder);

    /// <summary>Asks for a folder; null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>Asks where to save a .tga file; null when the user cancels.</summary>
    Task<string?> PickTgaSaveAsync(string suggestedName, string? startFolder);

    /// <summary>A yes / no question; "no" is the default answer.</summary>
    Task<bool> ConfirmAsync(string title, string message);
}

public sealed class WindowDialogService(Window owner) : IDialogService
{
    private IStorageProvider Storage => owner.StorageProvider;

    private async Task<IStorageFolder?> StartFolderAsync(string? folder) =>
        !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? await Storage.TryGetFolderFromPathAsync(folder) : null;

    public async Task<string?> PickLevelAsync(string? startFolder)
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open a Tomb Raider or TombEngine level",
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(startFolder),
            FileTypeFilter =
            [
                new FilePickerFileType("Levels") { Patterns = ["*.phd", "*.tub", "*.tr2", "*.tr4", "*.trc", "*.ten"] },
                new FilePickerFileType("Tomb Raider 1") { Patterns = ["*.phd", "*.tub"] },
                new FilePickerFileType("Tomb Raider 2 / 3") { Patterns = ["*.tr2"] },
                new FilePickerFileType("Tomb Raider 4") { Patterns = ["*.tr4"] },
                new FilePickerFileType("Tomb Raider 5") { Patterns = ["*.trc"] },
                new FilePickerFileType("TombEngine") { Patterns = ["*.ten"] },
                FilePickerFileTypes.All
            ]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title, string? startFolder)
    {
        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(startFolder)
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickTgaSaveAsync(string suggestedName, string? startFolder)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the level's texture atlas",
            SuggestedFileName = suggestedName,
            DefaultExtension = "tga",
            ShowOverwritePrompt = true,
            SuggestedStartLocation = await StartFolderAsync(startFolder),
            FileTypeChoices = [new FilePickerFileType("Targa image") { Patterns = ["*.tga"] }]
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> ConfirmAsync(string title, string message) =>
        await new ConfirmDialog(title, message).ShowDialog<bool>(owner);
}
