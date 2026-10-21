using Basalt.Application.Abstractions;
using Basalt.Application.Accounts;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Common;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>High-level sync state shown by the UI (spec 06 §12).</summary>
public enum SyncState
{
    NotConfigured,
    Disabled,
    Paused,
    Idle,
    Syncing,
    UpToDate,
    Error,
}

/// <summary>Observable snapshot of one vault's sync, combining the profile with the coordinator (spec 06 §12).</summary>
public sealed record SyncStatusView(
    VaultId VaultId,
    SyncState State,
    bool IsRunning,
    SyncCycleStatus? LastResult,
    Error? LastError,
    DateTimeOffset? LastFinished,
    DateTimeOffset? NextRetryAt,
    int ConsecutiveFailures,
    int PendingChanges = 0,
    int Conflicts = 0,
    int Quarantined = 0)
{
    public static SyncStatusView NotConfigured(VaultId vault) =>
        new(vault, SyncState.NotConfigured, false, null, null, null, null, 0);
}

/// <summary>Runs one manual cycle (spec 03 §2 and 06 §11).</summary>
public sealed record SyncVault(VaultId VaultId) : ICommand<SyncCycleResult?>, IVaultRequest;

/// <summary>Stops automatic triggers without changing the profile's repository (spec 03 §2).</summary>
public sealed record PauseSync(VaultId VaultId) : ICommand<Unit>, IVaultRequest;

/// <summary>Re-enables automatic triggers (spec 03 §2).</summary>
public sealed record ResumeSync(VaultId VaultId) : ICommand<Unit>, IVaultRequest;

/// <summary>Deletes the profile and sync state; never touches vault files or the repository (spec 03 §2).</summary>
public sealed record DisableSync(VaultId VaultId) : ICommand<Unit>, IVaultRequest;

public sealed record GetSyncStatus(VaultId VaultId) : IQuery<SyncStatusView>, IVaultRequest;

/// <summary>Last <paramref name="Max"/> entries, oldest first (spec 03 §2).</summary>
public sealed record GetSyncLog(VaultId VaultId, int Max = 50) : IQuery<IReadOnlyList<SyncLogEntry>>, IVaultRequest;

public sealed class SyncVaultHandler(
    ISyncProfileStore profiles,
    IGitHubTokenProvider tokens,
    IAccountStore accounts,
    IVaultRegistry registry,
    SyncCoordinator coordinator) : ICommandHandler<SyncVault, SyncCycleResult?>
{
    public async Task<Result<SyncCycleResult?>> HandleAsync(SyncVault command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await profiles.GetAsync(command.VaultId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return new Error(SyncErrors.NotConfigured, "The vault has no sync profile.");
        }

        var token = await tokens.GetTokenAsync(profile.AccountId, ct).ConfigureAwait(false);
        var account = await accounts.GetAsync(profile.AccountId, ct).ConfigureAwait(false);
        var registration = (await registry.GetAllAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(vault => vault.Id == command.VaultId);
        var author = new CommitAuthor(
            profile.Commit.AuthorNameOverride ?? account.ValueOrDefault()?.Login ?? "Basalt",
            profile.Commit.AuthorEmailOverride
                ?? (account.ValueOrDefault() is { } saved
                    ? $"{saved.Login}@users.noreply.github.com"
                    : "basalt@users.noreply.github.com"));
        var context = new SyncCycleContext(
            IsOnline: true,
            HasToken: token.IsSuccess,
            author,
            registration?.Name ?? "Vault");

        return await coordinator.RequestAsync(profile, SyncTriggerReason.Manual, context, ct).ConfigureAwait(false);
    }
}

public sealed class PauseSyncHandler(
    ISyncProfileStore profiles,
    SyncCoordinator coordinator) : ICommandHandler<PauseSync, Unit>
{
    public async Task<Result<Unit>> HandleAsync(PauseSync command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await profiles.GetAsync(command.VaultId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return new Error(SyncErrors.NotConfigured, "The vault has no sync profile.");
        }

        var paused = profile with { Paused = true };
        var saved = await profiles.SaveAsync(paused, ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await coordinator.StopAsync(command.VaultId).ConfigureAwait(false);
        return Result.Success();
    }
}

public sealed class ResumeSyncHandler(
    ISyncProfileStore profiles,
    SyncCoordinator coordinator) : ICommandHandler<ResumeSync, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResumeSync command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var profile = await profiles.GetAsync(command.VaultId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return new Error(SyncErrors.NotConfigured, "The vault has no sync profile.");
        }

        var resumed = profile with { Paused = false, Enabled = true };
        var saved = await profiles.SaveAsync(resumed, ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved;
        }

        await coordinator.StartAsync(resumed).ConfigureAwait(false);
        return Result.Success();
    }
}

public sealed class DisableSyncHandler(
    ISyncProfileStore profiles,
    ISyncStateStoreFactory states,
    SyncCoordinator coordinator) : ICommandHandler<DisableSync, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DisableSync command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        await coordinator.StopAsync(command.VaultId).ConfigureAwait(false);

        var deleted = await states.DeleteAsync(command.VaultId, ct).ConfigureAwait(false);
        if (deleted.IsFailure)
        {
            return deleted;
        }

        return await profiles.DeleteAsync(command.VaultId, ct).ConfigureAwait(false);
    }
}

public sealed class GetSyncStatusHandler(
    ISyncProfileStore profiles,
    SyncCoordinator coordinator) : IQueryHandler<GetSyncStatus, SyncStatusView>
{
    public async Task<Result<SyncStatusView>> HandleAsync(GetSyncStatus query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var profile = await profiles.GetAsync(query.VaultId, ct).ConfigureAwait(false);
        if (profile is null)
        {
            return SyncStatusView.NotConfigured(query.VaultId);
        }

        SyncStatus status;
        try
        {
            status = coordinator.GetStatus(query.VaultId);
        }
        catch (InvalidOperationException)
        {
            status = new SyncStatus(query.VaultId, false, null, null, 0, null);
        }

        var state = status.IsRunning
            ? SyncState.Syncing
            : !profile.Enabled
                ? SyncState.Disabled
                : profile.Paused
                    ? SyncState.Paused
                    : status.LastResult?.Status == SyncCycleStatus.Failed
                        ? SyncState.Error
                        : status.LastResult is null
                            ? SyncState.Idle
                            : SyncState.UpToDate;

        return new SyncStatusView(
            query.VaultId,
            state,
            status.IsRunning,
            status.LastResult?.Status,
            status.LastResult?.Error,
            status.LastFinished,
            status.NextAttempt,
            status.ConsecutiveFailures,
            Conflicts: status.LastResult?.Conflicts.Count ?? 0,
            Quarantined: 0);
    }
}

public sealed class GetSyncLogHandler(ISyncStateStoreFactory states) : IQueryHandler<GetSyncLog, IReadOnlyList<SyncLogEntry>>
{
    public async Task<Result<IReadOnlyList<SyncLogEntry>>> HandleAsync(GetSyncLog query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = await states.Create(query.VaultId).LoadLogAsync(Math.Max(1, query.Max), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<SyncLogEntry>>.Success(entries);
    }
}

file static class ResultAccountExtensions
{
    public static Domain.Accounts.GitHubAccount? ValueOrDefault(this Result<Domain.Accounts.GitHubAccount> result) =>
        result.IsSuccess ? result.Value : null;
}
