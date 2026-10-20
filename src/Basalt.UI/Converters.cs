using Avalonia.Data.Converters;
using Basalt.UI.Localization;

namespace Basalt.UI;

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
    /// <summary>Panel id (<c>files</c>, <c>search</c>…) → its localized title (<c>Panel.{id}</c>).</summary>
    public static IValueConverter PanelTitle { get; } = new FuncValueConverter<string?, string>(id => id is null ? string.Empty : LocalizationSource.Instance["Panel." + id]);
}
