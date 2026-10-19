using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>Editor preferences (spec 04 F-014).</summary>
public sealed record EditorSettings
{
    public string FontFamily { get; init; } = "Inter";

    public double FontSize { get; init; } = 15;

    public bool ReadableLineLength { get; init; } = true;

    public bool ShowLineNumbers { get; init; }

    public bool WordWrap { get; init; } = true;

    public bool SpellCheck { get; init; } = true;

    public bool AutoPairBrackets { get; init; } = true;
}

/// <summary>Application-wide settings of this device (<c>{AppData}/settings.json</c>).</summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    public ThemeMode Theme { get; init; } = ThemeMode.System;

    /// <summary>UI culture ("en", "es"); null follows the operating system.</summary>
    public string? Language { get; init; }

    public string? AccentColor { get; init; }

    public double InterfaceFontSize { get; init; } = 14;

    public bool OpenLastVault { get; init; } = true;

    public VaultId? LastVaultId { get; init; }

    /// <summary>User hotkey overrides: command id → gesture.</summary>
    public IReadOnlyDictionary<string, string> Hotkeys { get; init; } = new Dictionary<string, string>();

    public EditorSettings Editor { get; init; } = new();
}

/// <summary>Global settings of the app (spec 03 §1.6).</summary>
public interface IAppSettingsStore
{
    Task<AppSettings> GetAsync(CancellationToken ct);

    Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct);
}

/// <summary><see cref="IAppSettingsStore"/> over a document store.</summary>
public sealed class AppSettingsStore(IDocumentStore<AppSettings> store) : IAppSettingsStore
{
    public async Task<AppSettings> GetAsync(CancellationToken ct) => (await store.LoadAsync(ct).ConfigureAwait(false)).Value;

    public Task<Result<Unit>> SaveAsync(AppSettings settings, CancellationToken ct) => store.SaveAsync(settings, ct);
}
