using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Basalt.Application.Settings;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Settings;

namespace Basalt.UI.Views;

/// <summary>
/// Settings screen (spec 04 F-014). Every change is handed to its section ViewModel, which saves it immediately;
/// the controls follow the ViewModels when they refresh (language, theme, reload).
/// </summary>
public partial class SettingsView : UserControl
{
    private readonly ComboBox _language;
    private readonly CheckBox _openLastVault;
    private readonly TextBox _fontFamily;
    private readonly TextBox _fontSize;
    private readonly CheckBox _readableLineLength;
    private readonly CheckBox _showLineNumbers;
    private readonly CheckBox _wordWrap;
    private readonly CheckBox _spellCheck;
    private readonly CheckBox _autoPairBrackets;
    private readonly TextBox _attachmentFolder;
    private readonly TextBox _newNoteFolder;
    private readonly ComboBox _linkFormat;
    private readonly ComboBox _linkPathStyle;
    private readonly CheckBox _autoUpdateLinks;
    private readonly ComboBox _theme;
    private readonly TextBox _accentColor;
    private readonly TextBox _interfaceFontSize;
    private readonly TextBox _dailyFolder;
    private readonly TextBox _dailyDateFormat;
    private readonly TextBox _dailyTemplate;
    private readonly TextBox _templatesFolder;
    private SettingsViewModel? _settings;
    private bool _syncing;

    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        _language = this.FindControl<ComboBox>("Language")!;
        _openLastVault = this.FindControl<CheckBox>("OpenLastVault")!;
        _fontFamily = this.FindControl<TextBox>("FontFamilyBox")!;
        _fontSize = this.FindControl<TextBox>("FontSizeBox")!;
        _readableLineLength = this.FindControl<CheckBox>("ReadableLineLength")!;
        _showLineNumbers = this.FindControl<CheckBox>("ShowLineNumbers")!;
        _wordWrap = this.FindControl<CheckBox>("WordWrap")!;
        _spellCheck = this.FindControl<CheckBox>("SpellCheck")!;
        _autoPairBrackets = this.FindControl<CheckBox>("AutoPairBrackets")!;
        _attachmentFolder = this.FindControl<TextBox>("AttachmentFolder")!;
        _newNoteFolder = this.FindControl<TextBox>("NewNoteFolder")!;
        _linkFormat = this.FindControl<ComboBox>("LinkFormatBox")!;
        _linkPathStyle = this.FindControl<ComboBox>("LinkPathStyleBox")!;
        _autoUpdateLinks = this.FindControl<CheckBox>("AutoUpdateLinks")!;
        _theme = this.FindControl<ComboBox>("ThemeBox")!;
        _accentColor = this.FindControl<TextBox>("AccentColor")!;
        _interfaceFontSize = this.FindControl<TextBox>("InterfaceFontSize")!;
        _dailyFolder = this.FindControl<TextBox>("DailyFolder")!;
        _dailyDateFormat = this.FindControl<TextBox>("DailyDateFormat")!;
        _dailyTemplate = this.FindControl<TextBox>("DailyTemplate")!;
        _templatesFolder = this.FindControl<TextBox>("TemplatesFolder")!;
        DataContextChanged += (_, _) => Attach(DataContext as SettingsViewModel);
    }

    public SettingsViewModel? Settings => _settings;

    private void Attach(SettingsViewModel? settings)
    {
        if (_settings is not null)
        {
            _settings.General.PropertyChanged -= OnGeneralChanged;
            _settings.Appearance.PropertyChanged -= OnAppearanceChanged;
        }

        _settings = settings;
        if (settings is null)
        {
            return;
        }

        settings.General.PropertyChanged += OnGeneralChanged;
        settings.Appearance.PropertyChanged += OnAppearanceChanged;
        SyncGeneral();
        SyncAppearance();
        SyncVault();
    }

    private void OnGeneralChanged(object? sender, PropertyChangedEventArgs e) => SyncGeneral();

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => SyncAppearance();

    /// <summary>Refreshes the vault sections when their ViewModels appear (settings load after showing).</summary>
    private void SyncVault()
    {
        if (_settings is null)
        {
            return;
        }

        _syncing = true;
        _linkFormat.SelectedIndex = _settings.FilesAndLinks?.Settings.LinkFormat == LinkFormat.Markdown ? 1 : 0;
        _linkPathStyle.SelectedIndex = _settings.FilesAndLinks?.Settings.LinkPathStyle switch
        {
            LinkPathStyle.Relative => 1,
            LinkPathStyle.Absolute => 2,
            _ => 0,
        };
        _syncing = false;
    }

    private void SyncGeneral()
    {
        if (_settings is null)
        {
            return;
        }

        _syncing = true;
        _language.SelectedIndex = _settings.General.Language switch
        {
            "en" => 1,
            "es" => 2,
            _ => 0,
        };
        _openLastVault.IsChecked = _settings.General.OpenLastVault;
        _syncing = false;
    }

    private void SyncAppearance()
    {
        if (_settings is null)
        {
            return;
        }

        _syncing = true;
        _theme.SelectedIndex = _settings.Appearance.Theme switch
        {
            ThemeMode.Light => 1,
            ThemeMode.Dark => 2,
            _ => 0,
        };
        _accentColor.Text = _settings.Appearance.AccentColor ?? string.Empty;
        _interfaceFontSize.Text = _settings.Appearance.InterfaceFontSize.ToString(CultureInfo.CurrentCulture);
        _syncing = false;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _settings is null)
        {
            return;
        }

        _ = _settings.General.SetLanguageAsync(_language.SelectedIndex switch
        {
            1 => "en",
            2 => "es",
            _ => null,
        });
    }

    private void OnOpenLastVaultChanged(object? sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings is not null)
        {
            _ = _settings.General.SetOpenLastVaultAsync(_openLastVault.IsChecked == true);
        }
    }

    private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var family = _fontFamily.Text;
        var size = double.TryParse(_fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : _settings.Editor.Settings.FontSize;
        _ = _settings.Editor.UpdateAsync(s => s with
        {
            FontFamily = string.IsNullOrWhiteSpace(family) ? s.FontFamily : family,
            FontSize = size,
        });
    }

    private void OnEditorToggle(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || sender is not CheckBox box)
        {
            return;
        }

        var on = box.IsChecked == true;
        _ = _settings.Editor.UpdateAsync(s => box.Name switch
        {
            "ReadableLineLength" => s with { ReadableLineLength = on },
            "ShowLineNumbers" => s with { ShowLineNumbers = on },
            "WordWrap" => s with { WordWrap = on },
            "SpellCheck" => s with { SpellCheck = on },
            "AutoPairBrackets" => s with { AutoPairBrackets = on },
            _ => s,
        });
    }

    private void OnVaultFoldersLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_settings?.FilesAndLinks is not { } section)
        {
            return;
        }

        var attachments = _attachmentFolder.Text ?? string.Empty;
        var notes = _newNoteFolder.Text ?? string.Empty;
        _ = section.UpdateAsync(s => s with { AttachmentFolder = attachments, NewNoteFolder = notes });
    }

    private void OnLinkFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _settings?.FilesAndLinks is not { } section)
        {
            return;
        }

        var format = _linkFormat.SelectedIndex == 1 ? LinkFormat.Markdown : LinkFormat.Wikilink;
        _ = section.UpdateAsync(s => s with { LinkFormat = format });
    }

    private void OnLinkPathStyleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _settings?.FilesAndLinks is not { } section)
        {
            return;
        }

        var style = _linkPathStyle.SelectedIndex switch
        {
            1 => LinkPathStyle.Relative,
            2 => LinkPathStyle.Absolute,
            _ => LinkPathStyle.Shortest,
        };
        _ = section.UpdateAsync(s => s with { LinkPathStyle = style });
    }

    private void OnAutoUpdateLinksChanged(object? sender, RoutedEventArgs e)
    {
        if (!_syncing && _settings?.FilesAndLinks is { } section)
        {
            _ = section.UpdateAsync(s => s with { AutoUpdateLinks = _autoUpdateLinks.IsChecked == true });
        }
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _settings is null)
        {
            return;
        }

        var theme = _theme.SelectedIndex switch
        {
            1 => ThemeMode.Light,
            2 => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        _ = _settings.Appearance.UpdateAsync(theme, _settings.Appearance.AccentColor, _settings.Appearance.InterfaceFontSize);
    }

    private void OnAppearanceLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        var accent = string.IsNullOrWhiteSpace(_accentColor.Text) ? null : _accentColor.Text;
        var size = double.TryParse(_interfaceFontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            ? parsed
            : _settings.Appearance.InterfaceFontSize;
        _ = _settings.Appearance.UpdateAsync(_settings.Appearance.Theme, accent, size);
    }

    private void OnDailyNotesLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_settings?.DailyNotes is not { } section)
        {
            return;
        }

        var template = string.IsNullOrWhiteSpace(_dailyTemplate.Text) ? null : _dailyTemplate.Text;
        _ = section.UpdateAsync(new DailyNotesSettings(
            _dailyFolder.Text ?? string.Empty,
            _dailyDateFormat.Text ?? string.Empty,
            template));
    }

    private void OnTemplatesFolderLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_settings?.Templates is { } section)
        {
            _ = section.SetFolderAsync(_templatesFolder.Text ?? string.Empty);
        }
    }

    private void OnAssignHotkey(object? sender, RoutedEventArgs e)
    {
        if (_settings is null || sender is not Control button || button.DataContext is not HotkeyRow row)
        {
            return;
        }

        var box = (button.Parent as Grid)?.Children.OfType<TextBox>().FirstOrDefault();
        _ = _settings.Hotkeys.AssignAsync(row.CommandId, box?.Text ?? string.Empty);
    }

    private void OnResetHotkey(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null && sender is Control button && button.DataContext is HotkeyRow row)
        {
            _ = _settings.Hotkeys.ResetAsync(row.CommandId);
        }
    }

    private void OnRemoveAccount(object? sender, RoutedEventArgs e)
    {
        if (_settings?.Accounts is { } accounts)
        {
            _ = accounts.RemoveCommand.ExecuteAsync(null);
        }
    }

    private void OnAddAccount(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _ = _settings.AddAccountAsync(CancellationToken.None);
        }
    }

    private void OnVaultSyncConfigure(object? sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _ = _settings.ConfigureSyncAsync(CancellationToken.None);
        }
    }

    private void OnVaultSyncPause(object? sender, RoutedEventArgs e)
    {
        if (_settings?.VaultSync is { } sync)
        {
            _ = sync.PauseCommand.ExecuteAsync(null);
        }
    }

    private void OnVaultSyncResume(object? sender, RoutedEventArgs e)
    {
        if (_settings?.VaultSync is { } sync)
        {
            _ = sync.ResumeCommand.ExecuteAsync(null);
        }
    }

    private void OnVaultSyncDisable(object? sender, RoutedEventArgs e)
    {
        if (_settings?.VaultSync is { } sync)
        {
            _ = sync.DisableCommand.ExecuteAsync(null);
        }
    }
}
