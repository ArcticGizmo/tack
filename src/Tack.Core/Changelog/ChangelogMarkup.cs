using System.Text.RegularExpressions;

namespace Tack.Core.Changelog;

/// <summary>
/// Shared, UI-free helpers for the lightweight markdown the changelog uses. Kept in Core so the CLI's
/// Spectre renderer and the UI's Avalonia renderer strip inline markup identically.
/// </summary>
public static class ChangelogMarkup
{
    /// <summary>Strips inline markdown (bold/italic/code/links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"_(.*?)_", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
