using System.Collections.ObjectModel;
using Tack.App.Services;
using Tack.Core.Config;
using Tack.Core.Maintenance;

namespace Tack.App.ViewModels;

/// <summary>
/// The registry editor: register existing tool installs (tool@version -> binDir + exposed binaries), set the
/// default version, and drop registrations. Mirrors the CLI `tack tools add`; every change saves config.json
/// and reshims, exactly as the command does.
/// </summary>
public sealed class RegistryViewModel : ViewModelBase
{
    private readonly TackServices _services;
    private readonly IDialogService _dialogs;

    private string _newTool = "";
    private string _newVersion = "";
    private string _newBinDir = "";
    private string _newExposes = "";
    private string _status = "";
    private ToolVersionRow? _selected;

    public RegistryViewModel(TackServices services, IDialogService dialogs)
    {
        _services = services;
        _dialogs = dialogs;
        BrowseBinDirCommand = new AsyncRelayCommand(BrowseBinDirAsync);
        DetectExposesCommand = new RelayCommand(DetectExposes);
        RegisterCommand = new AsyncRelayCommand(RegisterAsync);
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Selected is not null);
        SetDefaultCommand = new AsyncRelayCommand(SetDefaultAsync, () => Selected is not null);
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<ToolVersionRow> Versions { get; } = new();

    public string NewTool { get => _newTool; set => SetField(ref _newTool, value); }
    public string NewVersion { get => _newVersion; set => SetField(ref _newVersion, value); }
    public string NewBinDir { get => _newBinDir; set => SetField(ref _newBinDir, value); }
    public string NewExposes { get => _newExposes; set => SetField(ref _newExposes, value); }
    public string Status { get => _status; private set => SetField(ref _status, value); }

    public ToolVersionRow? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
            {
                RemoveCommand.RaiseCanExecuteChanged();
                SetDefaultCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand BrowseBinDirCommand { get; }
    public RelayCommand DetectExposesCommand { get; }
    public AsyncRelayCommand RegisterCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }
    public AsyncRelayCommand SetDefaultCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void Refresh()
    {
        var config = _services.Load();
        Versions.Clear();
        foreach (var (toolName, tool) in config.Tools.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            string? def = config.Defaults.TryGetValue(toolName, out var d) ? d : null;
            foreach (var (version, iv) in tool.Versions.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                Versions.Add(new ToolVersionRow
                {
                    Tool = toolName,
                    Version = version,
                    BinDir = iv.BinDir,
                    Exposes = string.Join(", ", iv.Exposes),
                    IsDefault = string.Equals(def, version, StringComparison.OrdinalIgnoreCase),
                    BinDirExists = System.IO.Directory.Exists(iv.BinDir),
                });
            }
        }
    }

    private void DetectExposes()
    {
        if (string.IsNullOrWhiteSpace(NewBinDir) || !System.IO.Directory.Exists(NewBinDir))
        {
            Status = "Set an existing binDir first, then detect.";
            return;
        }
        var exposes = ToolProbe.DetectExposes(NewBinDir);
        NewExposes = string.Join(", ", exposes);
        Status = exposes.Count == 0 ? "No executables found in that directory." : $"Detected {exposes.Count} executable(s).";
    }

    private async Task RegisterAsync()
    {
        string tool = NewTool.Trim();
        string version = NewVersion.Trim();
        if (tool.Length == 0 || version.Length == 0)
        {
            Status = "Tool and version are required (e.g. node, 20.11.0).";
            return;
        }
        if (string.IsNullOrWhiteSpace(NewBinDir) || !System.IO.Directory.Exists(NewBinDir))
        {
            Status = "binDir must be an existing directory.";
            return;
        }

        string binDir = System.IO.Path.GetFullPath(NewBinDir);
        var exposes = SplitExposes(NewExposes);
        if (exposes.Count == 0) exposes = ToolProbe.DetectExposes(binDir);
        if (!exposes.Any(x => string.Equals(x, tool, StringComparison.OrdinalIgnoreCase)))
            exposes.Insert(0, tool); // the tool's own name must always be shimmed

        var result = await Task.Run(() =>
        {
            var config = _services.Load();
            if (!config.Tools.TryGetValue(tool, out var rt))
            {
                rt = new RegisteredTool();
                config.Tools[tool] = rt;
            }
            rt.Versions[version] = new InstalledVersion { BinDir = binDir, Exposes = exposes };
            if (!config.Defaults.ContainsKey(tool)) config.Defaults[tool] = version;
            _services.Save(config);
            return _services.Reshim(config);
        });

        Status = $"Registered {tool}@{version}. {Describe(result)}";
        NewVersion = "";
        NewBinDir = "";
        NewExposes = "";
        Refresh();
    }

    private async Task RemoveAsync()
    {
        var row = Selected;
        if (row is null) return;

        await Task.Run(() =>
        {
            var config = _services.Load();
            if (config.Tools.TryGetValue(row.Tool, out var rt))
            {
                rt.Versions.Remove(row.Version);
                if (rt.Versions.Count == 0)
                {
                    config.Tools.Remove(row.Tool);
                    config.Defaults.Remove(row.Tool);
                }
                else if (config.Defaults.TryGetValue(row.Tool, out var def)
                         && string.Equals(def, row.Version, StringComparison.OrdinalIgnoreCase))
                {
                    // Promote another registered version to default so the tool still resolves.
                    config.Defaults[row.Tool] = rt.Versions.Keys
                        .OrderByDescending(v => v, StringComparer.OrdinalIgnoreCase).First();
                }
                _services.Save(config);
                _services.Reshim(config);
            }
        });

        Status = $"Removed {row.Tool}@{row.Version}.";
        Refresh();
    }

    private async Task SetDefaultAsync()
    {
        var row = Selected;
        if (row is null) return;

        await Task.Run(() =>
        {
            var config = _services.Load();
            if (config.Tools.ContainsKey(row.Tool))
            {
                config.Defaults[row.Tool] = row.Version;
                _services.Save(config);
                _services.Reshim(config);
            }
        });

        Status = $"{row.Tool} default is now {row.Version}.";
        Refresh();
    }

    private async Task BrowseBinDirAsync()
    {
        var picked = await _dialogs.PickFolderAsync("Choose the tool's bin directory", NewBinDir);
        if (picked is not null) NewBinDir = picked;
    }

    private static List<string> SplitExposes(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Describe(ReshimResult r) => r.ShimPayloadMissing
        ? "resolved.json updated (no shims stamped - dev run)."
        : $"{r.ShimsWritten} shim(s) written, {r.ShimsPruned} pruned.";
}
