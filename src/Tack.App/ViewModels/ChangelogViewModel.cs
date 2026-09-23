using Tack.App.Changelog;
using Tack.Core.Changelog;

namespace Tack.App.ViewModels;

/// <summary>
/// The What's New tab: renders the full embedded <c>CHANGELOG.md</c>. Read-only. The heavy lifting (loading
/// the embedded text, parsing sections) is done once here; the view turns the sections into themed controls
/// via <see cref="ChangelogMarkdown"/>. The empty <c>[Unreleased]</c> section is dropped so the tab opens on
/// the latest actual release.
/// </summary>
public sealed class ChangelogViewModel : ViewModelBase
{
    public ChangelogViewModel()
    {
        var markdown = ChangelogMarkdown.LoadEmbedded();
        CurrentVersion = "v" + VersionInfo.Of(typeof(ChangelogViewModel).Assembly);

        if (markdown is null)
        {
            Sections = [];
            Status = "Changelog is unavailable (not embedded in this build).";
            return;
        }

        // Keep versioned sections, plus [Unreleased] only when it actually has content.
        Sections = ChangelogParser.Parse(markdown)
            .Where(s => s.Version is not null || s.Block.Count > 1)
            .ToList();
        Status = Sections.Count == 0 ? "No changelog entries yet." : $"{CurrentVersion} installed.";
    }

    /// <summary>Every section to render, newest first (the view walks this list).</summary>
    public IReadOnlyList<ChangelogSection> Sections { get; }

    public string CurrentVersion { get; }
    public string Status { get; }

    /// <summary>The tab is content-only; a refresh is a no-op (the changelog is baked into the build).</summary>
    public void Refresh() { }
}
