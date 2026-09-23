using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Tack.App.Changelog;
using Tack.App.ViewModels;

namespace Tack.App.Views;

public partial class ChangelogView : UserControl
{
    public ChangelogView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Populate();
        Populate();
    }

    // Rebuild the entries whenever the view model is (re)assigned. The changelog is static, so a single
    // render is enough - but keying off DataContext keeps this robust to the shell reusing view instances.
    private void Populate()
    {
        var host = this.FindControl<StackPanel>("Entries");
        if (host is null) return;
        host.Children.Clear();

        if (DataContext is not ChangelogViewModel vm) return;
        for (int i = 0; i < vm.Sections.Count; i++)
        {
            if (i > 0)
                host.Children.Add(new Border
                {
                    Height = 1, Margin = new Avalonia.Thickness(0, 12, 0, 12),
                    Background = Avalonia.Application.Current!.TryGetResource(
                        "BorderBrush", Avalonia.Application.Current.ActualThemeVariant, out var b) && b is Avalonia.Media.IBrush br
                        ? br : Avalonia.Media.Brushes.Gray,
                });
            ChangelogMarkdown.Render(host, vm.Sections[i].Block);
        }
    }
}
