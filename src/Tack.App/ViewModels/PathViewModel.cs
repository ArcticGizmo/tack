using System.Collections.ObjectModel;
using Tack.App.Services;
using Tack.Core.Config;

namespace Tack.App.ViewModels;

/// <summary>
/// The PATH doctor: runs Core's PathDoctor checks and visualises the effective PATH so the shims dir and
/// anything shadowing it are obvious. Read-only by design - PATH mutation stays at install time (the Velopack
/// callback), so the app never writes an EDR-sensitive HKCU\Environment change.
/// </summary>
public sealed class PathViewModel : ViewModelBase
{
    private readonly TackServices _services;
    private string _summary = "";
    private bool _healthy;

    public PathViewModel(TackServices services)
    {
        _services = services;
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<DoctorRow> Checks { get; } = new();
    public ObservableCollection<PathEntryRow> PathEntries { get; } = new();

    public string Summary { get => _summary; private set => SetField(ref _summary, value); }
    public bool Healthy { get => _healthy; private set => SetField(ref _healthy, value); }

    public RelayCommand RefreshCommand { get; }

    public void Refresh()
    {
        var config = _services.Load();

        Checks.Clear();
        var report = _services.Doctor(config);
        foreach (var c in report.Checks) Checks.Add(DoctorRow.From(c));
        Healthy = !report.HasProblems;
        Summary = report.HasProblems ? "tack doctor found issues below." : "All checks passed.";

        PathEntries.Clear();
        var names = ExposedNames(config);
        var entries = _services.PathEntries();
        int shimsIndex = -1;
        for (int i = 0; i < entries.Count; i++)
            if (TackServices.SamePath(entries[i], _services.ShimsDir)) { shimsIndex = i; break; }

        for (int i = 0; i < entries.Count; i++)
        {
            bool isShims = i == shimsIndex;
            string note = "";
            if (!isShims && shimsIndex >= 0 && i < shimsIndex)
            {
                var shadowed = names.FirstOrDefault(n => TackServices.LocateBinary(entries[i], n) is not null);
                if (shadowed is not null) note = $"shadows shims: provides '{shadowed}'";
            }
            PathEntries.Add(new PathEntryRow { Order = i + 1, Path = entries[i], IsShims = isShims, Note = note });
        }
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
