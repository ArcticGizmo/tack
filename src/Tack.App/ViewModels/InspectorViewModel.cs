using System.Collections.ObjectModel;
using Tack.App.Services;

namespace Tack.App.ViewModels;

/// <summary>
/// The "why is that IDE using the wrong node?" debugger. Pick any directory and see exactly which version
/// every tool resolves to there and the winning rule - the resolution a shim would reach if a process ran
/// in that directory, regardless of whether your shell was ever configured.
/// </summary>
public sealed class InspectorViewModel : ViewModelBase
{
    private readonly TackServices _services;
    private readonly IDialogService _dialogs;

    private string _directory = Environment.CurrentDirectory;
    private ResolutionRow? _selected;
    private string _detail = "Pick a directory, then select a tool to see the full explanation.";

    public InspectorViewModel(TackServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        InspectCommand = new RelayCommand(Inspect);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
    }

    public ObservableCollection<ResolutionRow> Rows { get; } = new();

    public string Directory
    {
        get => _directory;
        set => SetField(ref _directory, value);
    }

    public ResolutionRow? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) UpdateDetail(); }
    }

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    public RelayCommand InspectCommand { get; }
    public AsyncRelayCommand BrowseCommand { get; }

    public void Inspect()
    {
        var config = _services.Load();
        Rows.Clear();
        string dir = string.IsNullOrWhiteSpace(Directory) ? Environment.CurrentDirectory : Directory;
        foreach (var r in _services.ResolveAll(config, dir))
            Rows.Add(ResolutionRow.From(r));
        Selected = Rows.FirstOrDefault();
        if (Rows.Count == 0)
            Detail = "No tools registered - add one on the Registry tab.";
    }

    private void UpdateDetail()
    {
        if (Selected is null) return;
        Detail =
            $"Tool:    {Selected.Tool}\n" +
            $"Version: {Selected.Version}\n" +
            $"Source:  {Selected.SourceLabel}\n" +
            $"Why:     {(string.IsNullOrEmpty(Selected.Why) ? "-" : Selected.Why)}\n" +
            $"Binary:  {Selected.Binary}";
    }

    private async Task BrowseAsync()
    {
        var picked = await _dialogs.PickFolderAsync("Choose a directory to inspect", Directory);
        if (picked is not null)
        {
            Directory = picked;
            Inspect();
        }
    }
}
