using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Basalt.Presentation.Common;

namespace Basalt.UI;

/// <summary>
/// Finds the view of a ViewModel by convention: <c>Basalt.Presentation.X.FooViewModel</c> →
/// <c>Basalt.UI.Views.FooView</c>. A missing view shows the ViewModel's name (visible during development).
/// </summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var name = param.GetType().Name;
        var viewName = "Basalt.UI.Views." + (name.EndsWith("ViewModel", StringComparison.Ordinal) ? name[..^"ViewModel".Length] : name) + "View";
        return typeof(ViewLocator).Assembly.GetType(viewName) is { } view
            ? (Control)Activator.CreateInstance(view)!
            : new TextBlock { Text = name };
    }

    public bool Match(object? data) => data is ViewModelBase;
}
