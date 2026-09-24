using System.Collections.ObjectModel;
using Tack.App.Services;
using Tack.Core.Config;
using Tack.Core.Resolution;

namespace Tack.App.ViewModels;

/// <summary>
/// The zones editor: central directories (and everything under them) that resolve a tool version without a
/// repo tack.yml. Mirrors the CLI `tack zones`. Each row is one (directory -> tool@version) zone; an "enforced"
/// zone beats a repo tack.yml (org enforcement). Adds and removals save config.json and reshim.
/// </summary>
public sealed class ZonesViewModel : ViewModelBase
{
    private readonly TackServices _services;

    private string _newPath = "";
    private string _newTool = "";
    private string _newVersion = "";
    private bool _newEnforce;
    private string _status = "";
    private ZoneRow? _selected;

    public ZonesViewModel(TackServices services)
    {
        _services = services;
        AddCommand = new AsyncRelayCommand(AddAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Selected is not null);
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<ZoneRow> Zones { get; } = new();

    public string NewPath { get => _newPath; set => SetField(ref _newPath, value); }
    public string NewTool { get => _newTool; set => SetField(ref _newTool, value); }
    public string NewVersion { get => _newVersion; set => SetField(ref _newVersion, value); }
    public bool NewEnforce { get => _newEnforce; set => SetField(ref _newEnforce, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public ZoneRow? Selected
    {
        get => _selected;
        set { if (SetField(ref _selected, value)) RemoveCommand.RaiseCanExecuteChanged(); }
    }

    public AsyncRelayCommand AddCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void Refresh()
    {
        var config = _services.Load();
        Zones.Clear();
        foreach (var z in ZoneRegistry.Sorted(config))
        {
            Zones.Add(new ZoneRow
            {
                Path = z.Path,
                Tool = z.Tool,
                Version = z.Version,
                Enforce = z.Enforce,
            });
        }
    }

    private async Task AddAsync()
    {
        string path = NewPath.Trim();
        string tool = NewTool.Trim();
        string version = NewVersion.Trim();
        if (path.Length == 0 || tool.Length == 0 || version.Length == 0)
        {
            Status = "Directory, tool and version are all required (e.g. C:\\work , node, 18.19.0).";
            return;
        }
        if (ZonePath.Validate(path) is { } error)
        {
            Status = $"That directory won't work: {error}.";
            return;
        }

        bool enforce = NewEnforce;
        var result = await Task.Run(() =>
        {
            var config = _services.Load();
            var r = ZoneRegistry.Set(config, path, tool, version, enforce);
            _services.Save(config);
            _services.Reshim(config);
            return r;
        });

        string what = $"{path} -> {tool}@{version}{(enforce ? " (enforced)" : "")}";
        Status = result.Previous is { } p ? $"Updated zone {what} (was {p.Version})." : $"Added zone {what}.";
        NewPath = "";
        NewTool = "";
        NewVersion = "";
        NewEnforce = false;
        Refresh();
    }

    private async Task RemoveAsync()
    {
        var row = Selected;
        if (row is null) return;

        await Task.Run(() =>
        {
            var config = _services.Load();
            if (ZoneRegistry.Remove(config, row.Path, row.Tool).Count > 0)
            {
                _services.Save(config);
                _services.Reshim(config);
            }
        });

        Status = $"Removed zone {row.Path} -> {row.Tool}.";
        Refresh();
    }
}
