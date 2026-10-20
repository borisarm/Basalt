using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Workspace;
using Basalt.UI.Graph;

namespace Basalt.UI.Views;

public partial class GraphTabView : UserControl
{
    public GraphTabView()
    {
        AvaloniaXamlLoader.Load(this);
        var canvas = this.FindControl<GraphCanvas>("Canvas")!;
        canvas.NodeActivated += (_, e) =>
        {
            if (DataContext is GraphTabViewModel tab)
            {
                var command = e.NewTab ? tab.OpenNodeNewTabCommand : tab.OpenNodeCommand;
                command.Execute(e.Node);
            }
        };
    }
}
