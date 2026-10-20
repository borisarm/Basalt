using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Basalt.Presentation.Localization;

namespace Basalt.UI.Localization;

/// <summary>Bindable view of the localizer: <c>[key]</c> returns the text and updates when the culture changes.</summary>
public sealed class LocalizationSource : INotifyPropertyChanged
{
    private ILocalizer? _localizer;

    public static LocalizationSource Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => _localizer?.Get(key) ?? $"[{key}]";

    /// <summary>Connects the app's localizer (once, at start-up).</summary>
    public void Attach(ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (_localizer is not null)
        {
            _localizer.CultureChanged -= OnCultureChanged;
        }

        _localizer = localizer;
        localizer.CultureChanged += OnCultureChanged;
        OnCultureChanged(this, EventArgs.Empty);
    }

    /// <summary>Indexer bindings refresh on "Item" (Avalonia 12) or "Item[]" (the WPF convention).</summary>
    private void OnCultureChanged(object? sender, EventArgs e)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }
}

/// <summary><c>{l:Loc Key}</c>: a localized text that follows language changes (spec 07 §1).</summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding($"[{Key}]") { Source = LocalizationSource.Instance, Mode = BindingMode.OneWay };
}
