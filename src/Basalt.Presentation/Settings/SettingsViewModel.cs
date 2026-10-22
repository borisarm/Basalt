using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using Basalt.Application.Abstractions;
using Basalt.Application.Commands;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Settings;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Templates;
using Basalt.Domain.Vaults;
using Basalt.Presentation.Common;
using Basalt.Presentation.Input;
using Basalt.Presentation.Localization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Basalt.Presentation.Settings;

/// <summary>Applies the appearance settings to the running UI (implemented by the UI layer).</summary>
public interface IThemeService
{
    void Apply(ThemeMode theme, string? accentColor, double interfaceFontSize);
}

public sealed record SettingsDependencies(
    IAppSettingsStore Store,
    ICommandHandler<UpdateVaultSettings, Unit> UpdateVault,
    IThemeService Theme,
    IPlatformPaths Paths,
    KeyBindingService Bindings,
    ICommandRegistry Registry);

/// <summary>A section of the settings screen.</summary>
public sealed record SettingsSection(string Id, string TitleKey, object Content);

/// <summary>Serialised read-modify-save of <c>settings.json</c>; changes apply immediately (no "Save" button).</summary>
public sealed class AppSettingsEditor(IAppSettingsStore store) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = AppSettings.Default;

    public async Task LoadAsync(CancellationToken ct) => Current = await store.GetAsync(ct).ConfigureAwait(true);

    public void Dispose() => _gate.Dispose();

    public async Task<Result<Unit>> UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var updated = change(Current);
            var saved = await store.SaveAsync(updated, CancellationToken.None).ConfigureAwait(true);
            if (saved.IsSuccess)
            {
                Current = updated;
            }

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Serialised updates of the open vault's shared settings.</summary>
public sealed class VaultSettingsEditor(VaultSessionInfo vault, ICommandHandler<UpdateVaultSettings, Unit> update) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VaultSettings Current { get; private set; } = vault.Settings;

    public void Dispose() => _gate.Dispose();

    public async Task<Result<Unit>> UpdateAsync(Func<VaultSettings, VaultSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            var updated = change(Current);
            var saved = await update.HandleAsync(new UpdateVaultSettings(vault.Id, updated), CancellationToken.None).ConfigureAwait(true);
            if (saved.IsSuccess)
            {
                Current = updated;
            }

            return saved;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Language and start-up behaviour.</summary>
public sealed partial class GeneralSettingsViewModel(AppSettingsEditor editor, ILocalizer localizer) : ObservableObject
{
    /// <summary>"en", "es", or null to follow the system.</summary>
    [ObservableProperty]
    public partial string? Language { get; private set; }

    [ObservableProperty]
    public partial bool OpenLastVault { get; private set; }

    public void Refresh()
    {
        Language = editor.Current.Language;
        OpenLastVault = editor.Current.OpenLastVault;
    }

    public async Task SetLanguageAsync(string? language)
    {
        if ((await editor.UpdateAsync(s => s with { Language = language }).ConfigureAwait(true)).IsSuccess)
        {
            localizer.Culture = language is null ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(language);
            Refresh();
        }
    }

    public async Task SetOpenLastVaultAsync(bool value)
    {
        await editor.UpdateAsync(s => s with { OpenLastVault = value }).ConfigureAwait(true);
        Refresh();
    }
}

/// <summary>Editor preferences.</summary>
public sealed partial class EditorSettingsViewModel(AppSettingsEditor editor) : ObservableObject
{
    public const double MinFontSize = 8;
    public const double MaxFontSize = 40;

    [ObservableProperty]
    public partial EditorSettings Settings { get; private set; } = new();

    public void Refresh() => Settings = editor.Current.Editor;

    public async Task UpdateAsync(Func<EditorSettings, EditorSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        await editor.UpdateAsync(s =>
        {
            var updated = change(s.Editor);
            return s with { Editor = updated with { FontSize = Math.Clamp(updated.FontSize, MinFontSize, MaxFontSize) } };
        }).ConfigureAwait(true);
        Refresh();
    }
}

/// <summary>Theme, accent colour and interface size, applied immediately.</summary>
public sealed partial class AppearanceSettingsViewModel(AppSettingsEditor editor, IThemeService theme) : ObservableObject
{
    public const double MinInterfaceFontSize = 10;
    public const double MaxInterfaceFontSize = 24;

    [ObservableProperty]
    public partial ThemeMode Theme { get; private set; }

    [ObservableProperty]
    public partial string? AccentColor { get; private set; }

    [ObservableProperty]
    public partial double InterfaceFontSize { get; private set; }

    public void Refresh()
    {
        Theme = editor.Current.Theme;
        AccentColor = editor.Current.AccentColor;
        InterfaceFontSize = editor.Current.InterfaceFontSize;
    }

    public async Task UpdateAsync(ThemeMode mode, string? accent, double fontSize)
    {
        var size = Math.Clamp(fontSize, MinInterfaceFontSize, MaxInterfaceFontSize);
        if ((await editor.UpdateAsync(s => s with { Theme = mode, AccentColor = accent, InterfaceFontSize = size }).ConfigureAwait(true)).IsSuccess)
        {
            theme.Apply(mode, accent, size);
            Refresh();
        }
    }
}

/// <summary>Files and links (vault, synchronised): attachment and new-note folders, link format and style, link updates.</summary>
public sealed partial class FilesLinksSettingsViewModel(VaultSettingsEditor editor) : ObservableObject
{
    [ObservableProperty]
    public partial VaultSettings Settings { get; private set; } = editor.Current;

    public async Task UpdateAsync(Func<VaultSettings, VaultSettings> change)
    {
        await editor.UpdateAsync(change).ConfigureAwait(true);
        Settings = editor.Current;
    }
}

/// <summary>Daily notes (vault): folder, date format with a live preview, template.</summary>
public sealed partial class DailyNotesSettingsViewModel(VaultSettingsEditor editor, ILocalizer localizer, TimeProvider time) : ObservableObject
{
    [ObservableProperty]
    public partial DailyNotesSettings Settings { get; private set; } = editor.Current.DailyNotes;

    /// <summary>Today's date in the configured format.</summary>
    public string Preview => MomentFormat.Format(time.GetLocalNow().DateTime, Settings.DateFormat, localizer.Culture);

    public async Task UpdateAsync(DailyNotesSettings settings)
    {
        await editor.UpdateAsync(s => s with { DailyNotes = settings }).ConfigureAwait(true);
        Settings = editor.Current.DailyNotes;
        OnPropertyChanged(nameof(Preview));
    }
}

/// <summary>Templates folder (vault).</summary>
public sealed partial class TemplatesSettingsViewModel(VaultSettingsEditor editor) : ObservableObject
{
    [ObservableProperty]
    public partial string Folder { get; private set; } = editor.Current.TemplatesFolder;

    public async Task SetFolderAsync(string folder)
    {
        await editor.UpdateAsync(s => s with { TemplatesFolder = folder }).ConfigureAwait(true);
        Folder = editor.Current.TemplatesFolder;
    }
}

/// <summary>A command with its shortcut in the hotkeys section.</summary>
public sealed record HotkeyRow(string CommandId, string Title, string? Gesture, bool IsCustom, bool HasConflict);

/// <summary>Reassignable shortcuts with conflict detection (F-014).</summary>
public sealed class HotkeysSettingsViewModel(AppSettingsEditor editor, KeyBindingService bindings, ICommandRegistry registry, ILocalizer localizer) : ObservableObject
{
    public ObservableCollection<HotkeyRow> Rows { get; } = [];

    public void Refresh()
    {
        bindings.ApplyOverrides(editor.Current.Hotkeys);
        var conflicting = bindings.Conflicts.SelectMany(c => c.CommandIds).ToHashSet(StringComparer.Ordinal);
        Rows.Clear();
        foreach (var command in registry.Commands)
        {
            Rows.Add(new HotkeyRow(
                command.Id,
                localizer.Get(command.TitleKey),
                bindings.GestureFor(command.Id)?.ToString(bindings.Platform),
                bindings.Overrides.ContainsKey(command.Id),
                conflicting.Contains(command.Id)));
        }
    }

    /// <summary>Assigns a gesture (empty = none).</summary>
    /// <returns><c>gesture.invalid</c>, <c>command.not_found</c>.</returns>
    public Task<Result<Unit>> AssignAsync(string commandId, string gesture) => ChangeAsync(commandId, gesture);

    /// <summary>Back to the default gesture.</summary>
    public Task<Result<Unit>> ResetAsync(string commandId) => ChangeAsync(commandId, null);

    private async Task<Result<Unit>> ChangeAsync(string commandId, string? gesture)
    {
        var overrides = bindings.Reassign(commandId, gesture);
        if (overrides.IsFailure)
        {
            return overrides.Error;
        }

        var saved = await editor.UpdateAsync(s => s with { Hotkeys = overrides.Value }).ConfigureAwait(true);
        Refresh();
        return saved;
    }
}

/// <summary>Version, logs folder and licences.</summary>
public sealed class AboutViewModel(IPlatformPaths paths)
{
    public string Version { get; } = typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AboutViewModel).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public string LogsDirectory { get; } = paths.LogsDirectory;

    public IReadOnlyList<string> Licenses { get; } = ["Basalt — MIT", "Avalonia — MIT", "AvaloniaEdit — MIT", "Markdig — BSD-2-Clause", "YamlDotNet — MIT", "SQLite — Public domain", "CommunityToolkit.Mvvm — MIT"];
}

/// <summary>Accounts and per-vault sync sections (F-015), built by the composition root.</summary>
public sealed record SyncSettingsViewModels(
    Sync.AccountsSettingsViewModel Accounts,
    Sync.VaultSyncSettingsViewModel VaultSync);

/// <summary>The settings screen (F-014 and F-015).</summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly AppSettingsEditor _app;
    private readonly VaultSettingsEditor? _vault;
    private readonly VaultSessionInfo? _session;
    private readonly Shell.IWindowService? _windows;

    public SettingsViewModel(
        ViewModelServices services,
        SettingsDependencies dependencies,
        VaultSessionInfo? vault,
        SyncSettingsViewModels? sync = null,
        Shell.IWindowService? windows = null)
        : base(services)
    {
        _session = vault;
        _windows = windows;
        ArgumentNullException.ThrowIfNull(dependencies);
        _app = new AppSettingsEditor(dependencies.Store);
        General = new GeneralSettingsViewModel(_app, Localizer);
        Editor = new EditorSettingsViewModel(_app);
        Appearance = new AppearanceSettingsViewModel(_app, dependencies.Theme);
        Hotkeys = new HotkeysSettingsViewModel(_app, dependencies.Bindings, dependencies.Registry, Localizer);
        About = new AboutViewModel(dependencies.Paths);
        var sections = new List<SettingsSection>
        {
            new("general", "Settings.General", General),
            new("editor", "Settings.Editor", Editor),
        };
        if (vault is not null)
        {
            var vaultEditor = _vault = new VaultSettingsEditor(vault, dependencies.UpdateVault);
            FilesAndLinks = new FilesLinksSettingsViewModel(vaultEditor);
            DailyNotes = new DailyNotesSettingsViewModel(vaultEditor, Localizer, services.Time);
            Templates = new TemplatesSettingsViewModel(vaultEditor);
            sections.Add(new("files-links", "Settings.FilesAndLinks", FilesAndLinks));
        }

        sections.Add(new("appearance", "Settings.Appearance", Appearance));
        sections.Add(new("hotkeys", "Settings.Hotkeys", Hotkeys));
        if (vault is not null)
        {
            sections.Add(new("daily-notes", "Settings.DailyNotes", DailyNotes!));
            sections.Add(new("templates", "Settings.Templates", Templates!));
        }

        if (sync is not null)
        {
            Accounts = sync.Accounts;
            sections.Add(new("accounts", "Settings.Accounts", Accounts));
            if (vault is not null)
            {
                VaultSync = sync.VaultSync;
                sections.Add(new("sync", "Settings.Sync", VaultSync));
            }
        }

        sections.Add(new("about", "Settings.About", About));
        Sections = sections;
    }

    public IReadOnlyList<SettingsSection> Sections { get; }

    public GeneralSettingsViewModel General { get; }

    public EditorSettingsViewModel Editor { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    public HotkeysSettingsViewModel Hotkeys { get; }

    public AboutViewModel About { get; }

    public FilesLinksSettingsViewModel? FilesAndLinks { get; }

    public DailyNotesSettingsViewModel? DailyNotes { get; }

    public TemplatesSettingsViewModel? Templates { get; }

    public Sync.AccountsSettingsViewModel? Accounts { get; }

    public Sync.VaultSyncSettingsViewModel? VaultSync { get; }

    /// <summary>The iOS stack hosts the device flow as a page; desktop windows leave this null.</summary>
    public Func<CancellationToken, Task<bool>>? SignInPresenter { get; set; }

    /// <summary>Signs a GitHub account in (F-015.1) and refreshes the list.</summary>
    public async Task AddAccountAsync(CancellationToken ct)
    {
        if (Accounts is null)
        {
            return;
        }

        var signedIn = SignInPresenter is { } presenter
            ? await presenter(ct).ConfigureAwait(true)
            : _windows is not null && await _windows.ShowSignInAsync(ct).ConfigureAwait(true);
        if (signedIn)
        {
            await Accounts.LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Opens the sync setup wizard of this vault (F-015.2) and refreshes the section.</summary>
    public async Task ConfigureSyncAsync(CancellationToken ct)
    {
        if (_windows is null || _session is null || VaultSync is null)
        {
            return;
        }

        if (await _windows.ShowConfigureSyncAsync(_session, ct).ConfigureAwait(true))
        {
            await VaultSync.LoadAsync().ConfigureAwait(true);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _app.Dispose();
            _vault?.Dispose();
            Accounts?.Dispose();
            VaultSync?.Dispose();
        }

        base.Dispose(disposing);
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        await _app.LoadAsync(ct).ConfigureAwait(true);
        General.Refresh();
        Editor.Refresh();
        Appearance.Refresh();
        Hotkeys.Refresh();
        if (Accounts is { } accounts)
        {
            await accounts.LoadAsync().ConfigureAwait(true);
        }

        if (VaultSync is { } sync)
        {
            await sync.LoadAsync().ConfigureAwait(true);
        }
    }
}
