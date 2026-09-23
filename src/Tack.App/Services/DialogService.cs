using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Tack.App.Services;

/// <summary>Folder picker backed by the main window's StorageProvider (Avalonia's cross-toplevel file dialog API).</summary>
public sealed class DialogService : IDialogService
{
    private readonly TopLevel _topLevel;

    public DialogService(TopLevel topLevel) => _topLevel = topLevel;

    public async Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startAt))
        {
            try { start = await _topLevel.StorageProvider.TryGetFolderFromPathAsync(startAt); }
            catch { /* fall back to no suggested start */ }
        }

        var folders = await _topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        var folder = folders.Count > 0 ? folders[0] : null;
        return folder?.TryGetLocalPath();
    }
}
