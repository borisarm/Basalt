using Basalt.Application.Abstractions;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

public enum SyncLinkMode
{
    Init,
    Upload,
    Download,
    Merge,
}

/// <summary>Dry-run result shown before linking a vault to a repository (spec 06 §10).</summary>
public sealed record SyncLinkPreview(
    SyncLinkMode Mode,
    int Equal,
    int LocalOnly,
    int RemoteOnly,
    int Conflicts,
    bool IsPublic,
    bool CanPush,
    bool IsArchived);

public sealed record PreviewSyncLink(VaultId VaultId, GitHubAccountId AccountId, RemoteRepositoryRef Repository, SyncScope Scope, SyncPolicy Policy)
    : IQuery<SyncLinkPreview>, IVaultRequest;

public sealed record ConfigureSync(
    VaultId VaultId,
    GitHubAccountId AccountId,
    RemoteRepositoryRef Repository,
    SyncScope? Scope = null,
    SyncPolicy? Policy = null,
    SyncTriggers? Triggers = null,
    CommitSettings? Commit = null,
    bool AcceptMerge = false) : ICommand<SyncLinkPreview>, IVaultRequest;

public sealed class PreviewSyncLinkHandler(
    IVaultSessions sessions,
    IRepositoryAdministration administration,
    IRemoteRepositoryFactory repositories) : IQueryHandler<PreviewSyncLink, SyncLinkPreview>
{
    public async Task<Result<SyncLinkPreview>> HandleAsync(PreviewSyncLink query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var session = sessions.Get(query.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        return await PreviewAsync(
            session.Value,
            query.AccountId,
            query.Repository,
            query.Scope,
            query.Policy,
            administration,
            repositories,
            ct).ConfigureAwait(false);
    }

    internal static async Task<Result<SyncLinkPreview>> PreviewAsync(
        VaultSession session,
        GitHubAccountId account,
        RemoteRepositoryRef reference,
        SyncScope scope,
        SyncPolicy policy,
        IRepositoryAdministration administration,
        IRemoteRepositoryFactory repositories,
        CancellationToken ct)
    {
        var info = await administration.GetRepositoryAsync(account, reference.Owner, reference.Name, ct).ConfigureAwait(false);
        if (info.IsFailure)
        {
            return info.Error;
        }

        if (info.Value.IsArchived)
        {
            return new Error(RemoteErrors.Forbidden, "The repository is archived.");
        }

        if (!info.Value.CanPush)
        {
            return new Error(RemoteErrors.Forbidden, "The account cannot push to this repository.");
        }

        var scanner = new LocalSnapshotScanner(session.FileSystem, scope, policy, IgnoreRules.Parse([]));
        var local = await scanner.ScanAsync(HashCache.Empty, null, ct).ConfigureAwait(false);
        var repository = repositories.Create(account, reference);
        var head = await repository.GetHeadAsync(null, ct).ConfigureAwait(false);
        if (head.IsFailure)
        {
            return head.Error;
        }

        var remote = head.Value.Head is { } remoteHead
            ? await repository.GetTreeAsync(remoteHead.Tree, ct).ConfigureAwait(false)
            : Result<RemoteTreeSnapshot>.Success(new RemoteTreeSnapshot([], []));
        if (remote.IsFailure)
        {
            return remote.Error;
        }

        var localFiles = local.Manifest.Entries.Where(entry => !IsBasalt(entry.Path)).ToList();
        var remoteFiles = remote.Value.Files.Where(file => !IsBasalt(file.Path)).ToList();
        var localByPath = localFiles.ToDictionary(entry => entry.Path);
        var remoteByPath = remoteFiles.ToDictionary(file => file.Path);
        var equal = localByPath.Count(pair => remoteByPath.TryGetValue(pair.Key, out var other) && other.Sha == pair.Value.Sha);
        var localOnly = localByPath.Keys.Count(path => !remoteByPath.ContainsKey(path));
        var remoteOnly = remoteByPath.Keys.Count(path => !localByPath.ContainsKey(path));
        var conflicts = localByPath.Count(pair => remoteByPath.TryGetValue(pair.Key, out var other) && other.Sha != pair.Value.Sha);
        var mode = (localFiles.Count, remoteFiles.Count) switch
        {
            (0, 0) => SyncLinkMode.Init,
            (_, 0) => SyncLinkMode.Upload,
            (0, _) => SyncLinkMode.Download,
            _ => SyncLinkMode.Merge,
        };

        return new SyncLinkPreview(mode, equal, localOnly, remoteOnly, conflicts, !info.Value.IsPrivate, info.Value.CanPush, info.Value.IsArchived);
    }

    private static bool IsBasalt(VaultPath path) =>
        path.Segments.Count > 0 && string.Equals(path.Segments[0], ".basalt", StringComparison.OrdinalIgnoreCase);
}

public sealed class ConfigureSyncHandler(
    IVaultSessions sessions,
    ISyncProfileStore profiles,
    IRepositoryAdministration administration,
    IRemoteRepositoryFactory repositories,
    SyncCoordinator coordinator) : ICommandHandler<ConfigureSync, SyncLinkPreview>
{
    public async Task<Result<SyncLinkPreview>> HandleAsync(ConfigureSync command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var session = sessions.Get(command.VaultId);
        if (session.IsFailure)
        {
            return session.Error;
        }

        var scope = command.Scope ?? SyncScope.Default;
        var policy = command.Policy ?? SyncPolicy.Default;
        var preview = await PreviewSyncLinkHandler.PreviewAsync(
            session.Value, command.AccountId, command.Repository, scope, policy, administration, repositories, ct).ConfigureAwait(false);
        if (preview.IsFailure)
        {
            return preview.Error;
        }

        if (preview.Value.Mode == SyncLinkMode.Merge && !command.AcceptMerge)
        {
            return new Error(SyncErrors.ConfirmationRequired, "The merge needs explicit confirmation.");
        }

        var profile = new SyncProfile(
            command.VaultId,
            command.AccountId,
            command.Repository,
            Enabled: true,
            Paused: false,
            command.Triggers ?? new SyncTriggers(),
            policy,
            scope,
            command.Commit ?? new CommitSettings());
        var saved = await profiles.SaveAsync(profile, ct).ConfigureAwait(false);
        if (saved.IsFailure)
        {
            return saved.Error;
        }

        session.Value.Set(repositories.Create(command.AccountId, command.Repository));
        await coordinator.StartAsync(profile).ConfigureAwait(false);
        return preview.Value;
    }
}
