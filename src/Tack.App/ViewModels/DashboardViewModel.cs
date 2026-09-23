using System.Collections.ObjectModel;
using Tack.App.Services;

namespace Tack.App.ViewModels;

/// <summary>The landing screen: a live summary of tack's state plus how every registered tool resolves for a
/// chosen directory (defaults to where the process launched). Read-only - the "what does tack think" view.</summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly TackServices _services;
    private readonly IDialogService _dialogs;

    private string _directory = Environment.CurrentDirectory;
    private string _summary = "";
    private string _shimsStatus = "";
    private bool _shimsOnPath;

    public DashboardViewModel(TackServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(Refresh);
        UseCurrentCommand = new RelayCommand(() => { Directory = Environment.CurrentDirectory; Refresh(); });
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
    }

    public ObservableCollection<ResolutionRow> Rows { get; } = new();

    public string Directory
    {
        get => _directory;
        set => SetField(ref _directory, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetField(ref _summary, value);
    }

    public string ShimsStatus
    {
        get => _shimsStatus;
        private set => SetField(ref _shimsStatus, value);
    }

    public bool ShimsOnPath
    {
        get => _shimsOnPath;
        private set => SetField(ref _shimsOnPath, value);
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UseCurrentCommand { get; }
    public AsyncRelayCommand BrowseCommand { get; }

    public void Refresh()
    {
        var config = _services.Load();
        int versions = config.Tools.Values.Sum(t => t.Versions.Count);
        Summary = $"{config.Tools.Count} tool(s), {versions} version(s), {config.Bindings.Count} binding(s), "
                  + $"{config.Defaults.Count} default(s).";

        var report = _services.Doctor(config);
        ShimsOnPath = report.Checks.Any(c => c.Title == "Shims directory is on PATH"
                                             && c.Status == Tack.Core.Maintenance.CheckStatus.Ok);
        ShimsStatus = ShimsOnPath ? "shims dir is on PATH" : "shims dir is NOT on PATH - see the PATH tab";

        Rows.Clear();
        foreach (var r in _services.ResolveAll(config, string.IsNullOrWhiteSpace(Directory)
                     ? Environment.CurrentDirectory : Directory))
            Rows.Add(ResolutionRow.From(r));
    }

    private async Task BrowseAsync()
    {
        var picked = await _dialogs.PickFolderAsync("Choose a directory to inspect", Directory);
        if (picked is not null)
        {
            Directory = picked;
            Refresh();
        }
    }
}
