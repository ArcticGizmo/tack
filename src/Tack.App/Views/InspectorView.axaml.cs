using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Tack.App.Views;

public partial class InspectorView : UserControl
{
    public InspectorView() => AvaloniaXamlLoader.Load(this);
}
