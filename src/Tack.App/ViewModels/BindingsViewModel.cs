using System.Collections.ObjectModel;
using Tack.App.Services;
using Tack.Core.Config;

namespace Tack.App.ViewModels;

/// <summary>
/// The bindings editor: the central directory-glob rules that resolve a tool version without a repo tack.yml.
/// Mirrors the CLI `tack bind`. Each row is one (glob -> tool@version) rule; an "enforced" rule beats a repo
/// tack.yml (org enforcement). Adds and removals save config.json and reshim.
/// </summary>
public sealed class BindingsViewModel : ViewModelBase
{
    private readonly TackServices _services;

    private string _newGlob = "";
    private string _newTool = "";
    private string _newVersion = "";
    private bool _newEnforce;
    private string _status = "";
    private BindingRow? _selected;

    public BindingsViewModel(TackServices services)
    {
        _services = services;
        AddCommand = new AsyncRelayCommand(AddAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Selected is not null);
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<BindingRow> Bindings { get; } = new();

    public string NewGlob { get => _newGlob; set => SetField(ref _newGlob, value); }
    public string NewTool { get => _newTool; set => SetField(ref _newTool, value); }
    public string NewVersion { get => _newVersion; set => SetField(ref _newVersion, value); }
    public bool NewEnforce { get => _newEnforce; set => SetField(ref _newEnforce, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public BindingRow? Selected
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
        Bindings.Clear();
        for (int i = 0; i < config.Bindings.Count; i++)
        {
            var b = config.Bindings[i];
            foreach (var (tool, version) in b.Tools.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Bindings.Add(new BindingRow
                {
                    Index = i,
                    Glob = b.Glob,
                    Tool = tool,
                    Version = version,
                    Enforce = b.Enforce,
                });
            }
        }
    }

    private async Task AddAsync()
    {
        string glob = NewGlob.Trim();
        string tool = NewTool.Trim();
        string version = NewVersion.Trim();
        if (glob.Length == 0 || tool.Length == 0 || version.Length == 0)
        {
            Status = "Glob, tool and version are all required (e.g. C:/work/** , node, 18.19.0).";
            return;
        }

        await Task.Run(() =>
        {
            var config = _services.Load();
            config.Bindings.Add(new Binding
            {
                Glob = glob,
                Tools = { [tool] = version },
                Enforce = NewEnforce,
            });
            _services.Save(config);
            _services.Reshim(config);
        });

        Status = $"Bound {glob} -> {tool}@{version}{(NewEnforce ? " (enforced)" : "")}.";
        NewGlob = "";
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
            if (row.Index >= 0 && row.Index < config.Bindings.Count)
            {
                var b = config.Bindings[row.Index];
                b.Tools.Remove(row.Tool);
                if (b.Tools.Count == 0) config.Bindings.RemoveAt(row.Index);
                _services.Save(config);
                _services.Reshim(config);
            }
        });

        Status = $"Removed binding {row.Glob} -> {row.Tool}.";
        Refresh();
    }
}
