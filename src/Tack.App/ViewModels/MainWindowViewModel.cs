using Tack.App.Services;

namespace Tack.App.ViewModels;

/// <summary>
/// The window shell. Owns one view model per screen and re-reads that screen's data whenever its tab is
/// selected, so a change made on one tab (register a tool, add a zone, reshim) is reflected the moment you
/// switch to a tab that depends on it - without cross-view-model coupling.
/// </summary>
public sealed class MainWindowViewModel : ViewModelBase
{
    private int _selectedTabIndex;

    public MainWindowViewModel(TackServices services, IDialogService dialogs)
    {
        Dashboard = new DashboardViewModel(services, dialogs);
        Inspector = new InspectorViewModel(services, dialogs);
        Registry = new RegistryViewModel(services, dialogs);
        Zones = new ZonesViewModel(services);
        Path = new PathViewModel(services);
        Changelog = new ChangelogViewModel();

        SelectCommand = new RelayCommand<string>(s =>
        {
            if (int.TryParse(s, out var i)) SelectedTabIndex = i;
        });

        Dashboard.Refresh(); // land on a populated dashboard
    }

    /// <summary>Nav-rail selection: each rail button passes its index as CommandParameter.</summary>
    public RelayCommand<string> SelectCommand { get; }

    /// <summary>True under a dev build - the rail shows a "(Dev)" flag so the isolated data space is obvious.</summary>
    public bool IsDev => Tack.Core.TackProfile.IsDev;

    public DashboardViewModel Dashboard { get; }
    public InspectorViewModel Inspector { get; }
    public RegistryViewModel Registry { get; }
    public ZonesViewModel Zones { get; }
    public PathViewModel Path { get; }
    public ChangelogViewModel Changelog { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetField(ref _selectedTabIndex, value)) RefreshSelected();
        }
    }

    private void RefreshSelected()
    {
        switch (_selectedTabIndex)
        {
            case 0: Dashboard.Refresh(); break;
            case 1: Inspector.Inspect(); break;
            case 2: Registry.Refresh(); break;
            case 3: Zones.Refresh(); break;
            case 4: Path.Refresh(); break;
            case 5: Changelog.Refresh(); break;
        }
    }
}
