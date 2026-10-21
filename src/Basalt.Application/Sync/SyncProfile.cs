using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Per-vault sync profile stored on this device (spec 06 §2).</summary>
public sealed record SyncProfile(
    VaultId VaultId,
    GitHubAccountId AccountId,
    RemoteRepositoryRef Repository,
    bool Enabled,
    bool Paused,
    SyncTriggers Triggers,
    SyncPolicy Policy,
    SyncScope Scope,
    CommitSettings Commit);

/// <summary>When automatic sync runs (spec 06 §2).</summary>
public sealed record SyncTriggers(
    bool OnVaultOpen = true,
    TimeSpan? Interval = null,
    TimeSpan? AfterEditIdle = null,
    bool OnAppBackground = true,
    bool BackgroundRefresh = true,
    bool OnlyOnUnmeteredNetwork = false);

/// <summary>Commit message template, author overrides and version trailers (spec 06 §2 and §8).</summary>
public sealed record CommitSettings(
    string MessageTemplate = "Sync from {device}: {summary}",
    string? AuthorNameOverride = null,
    string? AuthorEmailOverride = null);
