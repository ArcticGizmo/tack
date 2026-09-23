using System.Collections.ObjectModel;
using Tack.App.Services;
using Tack.Core.Config;
using Tack.Core.Maintenance;

namespace Tack.App.ViewModels;

/// <summary>
/// The shims panel: lists the generated shim exes, flags stale ones (a name no longer exposed), and
/// regenerates them. Reshim recompiles config.json -> resolved.json and stamps one shim copy per exposed
/// name; under a dev run the shim binary isn't co-located, so resolved.json is written but nothing stamped.
/// </summary>
public sealed class ShimsViewModel : ViewModelBase
{
    private readonly TackServices _services;
    private string _status = "";
    private bool _exists;

    public ShimsViewModel(TackServices services)
    {
        _services = services;
        ReshimCommand = new AsyncRelayCommand(ReshimAsync);
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<ShimRow> Shims { get; } = new();

    public string ShimsDir => _services.ShimsDir;
    public string Status { get => _status; private set => SetField(ref _status, value); }
    public bool Exists { get => _exists; private set => SetField(ref _exists, value); }

    public AsyncRelayCommand ReshimCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public void Refresh()
    {
        var config = _services.Load();
        var names = ExposedNames(config);

        Shims.Clear();
        Exists = System.IO.Directory.Exists(ShimsDir);
        if (!Exists)
        {
            Status = "Shims directory does not exist yet - reshim to create it.";
            return;
        }

        var exes = System.IO.Directory.GetFiles(ShimsDir, "*.exe")
            .Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        int stale = 0;
        foreach (var name in exes)
        {
            bool isStale = !names.Contains(name!);
            if (isStale) stale++;
            Shims.Add(new ShimRow { Name = name!, Stale = isStale });
        }

        Status = Shims.Count == 0
            ? "No shims generated - register a tool, or reshim."
            : $"{Shims.Count} shim(s){(stale > 0 ? $", {stale} stale" : "")}.";
    }

    private async Task ReshimAsync()
    {
        var result = await Task.Run(() => _services.Reshim(_services.Load()));
        Status = result.ShimPayloadMissing
            ? "resolved.json updated, but tack-shim.exe wasn't found beside tack-ui (expected under a dev run) - no shims stamped."
            : $"Reshimmed: {result.ShimsWritten} written, {result.ShimsPruned} pruned.";
        Refresh();
    }

    private static HashSet<string> ExposedNames(CentralConfig c)
    {
        var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in c.Tools.Values)
            foreach (var v in t.Versions.Values)
                foreach (var e in v.Exposes)
                    s.Add(e);
        return s;
    }
}
