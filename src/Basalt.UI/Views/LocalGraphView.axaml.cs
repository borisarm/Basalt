using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Panels;
using Basalt.UI.Graph;

namespace Basalt.UI.Views;

public partial class LocalGraphView : UserControl
{
    public LocalGraphView()
    {
        AvaloniaXamlLoader.Load(this);
        var canvas = this.FindControl<GraphCanvas>("Canvas")!;
        canvas.NodeActivated += (_, e) =>
        {
            if (DataContext is LocalGraphViewModel panel)
            {
                panel.OpenNodeCommand.Execute(e.Node);
            }
        };
    }
}
