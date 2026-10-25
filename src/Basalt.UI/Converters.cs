using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Basalt.UI.Localization;

namespace Basalt.UI;

/// <summary>Value converters used by the views.</summary>
public static class Converters
{
    private const int AvatarColours = 4;

    /// <summary>Panel id (<c>files</c>, <c>search</c>…) → its localized title (<c>Panel.{id}</c>).</summary>
    public static IValueConverter PanelTitle { get; } = new FuncValueConverter<string?, string>(id => id is null ? string.Empty : LocalizationSource.Instance["Panel." + id]);

    /// <summary>
    /// True when the value, as text, equals the converter parameter: marks the active panel or a state class
    /// (<c>Classes.active="{Binding ActivePanel, Converter=…, ConverterParameter=files}"</c>).
    /// </summary>
    public static IValueConverter EqualsParameter { get; } = new FuncValueConverter<object?, object?, bool>(
        (value, parameter) => value is not null && string.Equals(
            Convert.ToString(value, CultureInfo.InvariantCulture), Convert.ToString(parameter, CultureInfo.InvariantCulture), StringComparison.Ordinal));

    /// <summary>The first letter of a name, upper case, for the vault avatar.</summary>
    public static IValueConverter Initial { get; } = new FuncValueConverter<string?, string>(
        name => string.IsNullOrWhiteSpace(name) ? "?" : name.Trim()[..1].ToUpper(CultureInfo.CurrentCulture));

    /// <summary>A stable colour per name from the graph group palette, so each vault keeps its avatar colour.</summary>
    public static IValueConverter AvatarBrush { get; } = new FuncValueConverter<string?, IBrush?>(name =>
    {
        var hash = 0;
        foreach (var c in name ?? string.Empty)
        {
            hash = unchecked((hash * 31) + c);
        }

        var key = $"BasaltGraphGroup{(hash & int.MaxValue) % AvatarColours}Brush";
        return Avalonia.Application.Current?.TryFindResource(key, Avalonia.Application.Current.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : null;
    });
}
