using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Tack.App.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base so the app carries no MVVM-toolkit dependency (matches the
/// project's lean-dependency stance). SetField raises a change only when the value actually differs.</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}
