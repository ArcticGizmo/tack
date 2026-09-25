using System.Text.RegularExpressions;

namespace Tack.Core.Changelog;

/// <summary>
/// Helpers for the lightweight markdown the changelog uses. Kept in Core, free of Spectre, so it stays
/// unit-testable on its own.
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
