namespace Tack.App.Services;

/// <summary>Abstracts the folder picker so view models stay free of Avalonia control references and testable.</summary>
public interface IDialogService
{
    /// <summary>Show a folder picker; returns the chosen absolute path, or null if cancelled.</summary>
    Task<string?> PickFolderAsync(string title, string? startAt = null);
}
