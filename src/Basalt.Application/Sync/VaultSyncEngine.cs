using Basalt.Application.Common;
using Basalt.Application.Errors;
using Basalt.Application.Ports.Events;
using Basalt.Application.Ports.FileSystem;
using Basalt.Application.Ports.Platform;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Storage;
using Basalt.Domain.Common;
using Basalt.Domain.Notes;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>Data the engine needs for one cycle; the caller has already checked account/token/network.</summary>
public sealed record SyncCycleRequest(
    SyncProfile Profile,
    bool IsOnline,
    bool HasToken,
    CommitAuthor Author,
    string VaultName,
    string BasaltVersion = "1.0.0");

public enum SyncCycleStatus
{
    UpToDate,
    Published,
    Downloaded,
    Merged,
    Disabled,
    Paused,
    Offline,
    AuthRequired,
    AlreadyRunning,
    Cancelled,
    Failed,
}

/// <summary>Outcome of a cycle. <see cref="Error"/> is set only for <see cref="SyncCycleStatus.Failed"/>.</summary>
public sealed record SyncCycleResult(
    SyncCycleStatus Status,
    SyncBase Base,
    IReadOnlyList<VaultPath> Conflicts,
    bool HasDeferred,
    Error? Error = null);

/// <summary>
/// Runs steps 0–10 of spec 06 §6 for one vault: preconditions and sync lease, flush, scan, remote snapshot, plan,
/// materialise, journal, publish (with re-planning on non-fast-forward), local apply and state commit.
/// </summary>
public sealed class VaultSyncEngine : IVaultSyncRunner
{
    private readonly VaultId _vault;
    private readonly IVaultFileSystem _fileSystem;
    private readonly IRemoteRepository _repository;
    private readonly ISyncStateStore _state;
    private readonly IEventBus _events;
    private readonly IClock _clock;
    private readonly IDeviceInfo _device;
    private readonly TimeProvider _time;
    private readonly SyncLocks _locks;
    private readonly Func<VaultId, CancellationToken, Task>? _flushEdits;
    private readonly VaultLocks _vaultLocks;
    private RemoteSnapshotProvider? _provider;

    public VaultSyncEngine(
        VaultId vault,
        IVaultFileSystem fileSystem,
        IRemoteRepository repository,
        ISyncStateStore state,
        IEventBus events,
        IClock clock,
        IDeviceInfo device,
        TimeProvider time,
        SyncLocks locks,
        VaultLocks vaultLocks,
        Func<VaultId, CancellationToken, Task>? flushEdits = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(locks);
        ArgumentNullException.ThrowIfNull(vaultLocks);
        _vault = vault;
        _fileSystem = fileSystem;
        _repository = repository;
        _state = state;
        _events = events;
        _clock = clock;
        _device = device;
        _time = time;
        _locks = locks;
        _vaultLocks = vaultLocks;
        _flushEdits = flushEdits;
    }

    public async Task<SyncCycleResult> RunAsync(SyncCycleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Profile.Enabled)
        {
            return new SyncCycleResult(SyncCycleStatus.Disabled, SyncBase.Empty, [], false);
        }

        if (request.Profile.Paused)
        {
            return new SyncCycleResult(SyncCycleStatus.Paused, SyncBase.Empty, [], false);
        }

        if (!request.IsOnline)
        {
            return new SyncCycleResult(SyncCycleStatus.Offline, SyncBase.Empty, [], false);
        }

        if (!request.HasToken)
        {
            return new SyncCycleResult(SyncCycleStatus.AuthRequired, SyncBase.Empty, [], false);
        }

        IDisposable? lease;
        try
        {
            lease = await _locks.TryAcquireAsync(_vault, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new SyncCycleResult(SyncCycleStatus.Cancelled, SyncBase.Empty, [], false);
        }

        if (lease is null)
        {
            return new SyncCycleResult(SyncCycleStatus.AlreadyRunning, SyncBase.Empty, [], false);
        }

        var started = _clock.UtcNow;
        var baseline = SyncBase.Empty;
        using (lease)
        {
            try
            {
                baseline = await _state.LoadBaseAsync(ct).ConfigureAwait(false);
                var cache = await _state.LoadHashCacheAsync(ct).ConfigureAwait(false);
                await FlushAsync(ct).ConfigureAwait(false);
                var provider = await ProviderAsync(ct).ConfigureAwait(false);
                var prepared = await PrepareAsync(request, baseline, cache, provider, ct).ConfigureAwait(false);
                if (prepared.IsFailure)
                {
                    return new SyncCycleResult(SyncCycleStatus.Failed, baseline, [], false, prepared.Error);
                }

                var latest = prepared.Value;
                var published = false;
                RemoteHead? head;
                if (latest.Plan.Actions.All(action => action is SyncAction.NoOp))
                {
                    head = latest.Remote.Head;
                }
                else
                {
                    var journal = new SyncJournal(
                        1,
                        latest.Remote.Head?.Commit,
                        latest.Plan.ExpectedNewBase,
                        [.. latest.Plan.Actions.Select(SyncJournalAction.From)],
                        started);
                    var saved = await _state.SaveJournalAsync(journal, ct).ConfigureAwait(false);
                    if (saved.IsFailure)
                    {
                        return new SyncCycleResult(SyncCycleStatus.Failed, baseline, [], false, saved.Error);
                    }

                    var publisher = new RemotePublisher(_fileSystem, _repository, _time);
                    var options = new PublishOptions(Message(request, latest), Author(request));
                    var publish = await publisher.PublishAsync(
                        new PublishRequest(latest.Materialized, latest.Remote, baseline.Manifest, latest.Protected),
                        options,
                        async refreshCt =>
                        {
                            var refreshed = await PrepareAsync(request, baseline, latest.Local.HashCache, provider, refreshCt)
                                .ConfigureAwait(false);
                            if (refreshed.IsFailure)
                            {
                                return Result<PublishRequest>.Failure(refreshed.Error);
                            }

                            latest = refreshed.Value;
                            return new PublishRequest(
                                latest.Materialized,
                                latest.Remote,
                                baseline.Manifest,
                                latest.Protected);
                        },
                        ct).ConfigureAwait(false);
                    if (publish.IsFailure)
                    {
                        return new SyncCycleResult(SyncCycleStatus.Failed, baseline, [], false, publish.Error);
                    }

                    head = publish.Value.Head;
                    published = publish.Value.Published;
                }

                var apply = await new LocalApplier(_fileSystem, _vaultLocks, _events)
                    .ApplyAsync(_vault, latest.Local.Manifest, latest.Materialized.Target, latest.Materialized.Contents, ct)
                    .ConfigureAwait(false);
                if (apply.IsFailure)
                {
                    return new SyncCycleResult(SyncCycleStatus.Failed, baseline, [], false, apply.Error);
                }

                var outcome = Outcome(latest, published);
                var conflicts = latest.Materialized.Conflicts.Select(conflict => conflict.Path).Distinct().Order().ToList();
                var commit = await new SyncStateCommitter(_state, _fileSystem, _clock)
                    .CommitAsync(
                        new SyncCommitRequest(
                            baseline,
                            latest.Materialized.Target,
                            head,
                            apply.Value.Deferred,
                            latest.Local.HashCache,
                            latest.Plan.Actions.Count(action => action is SyncAction.Download),
                            latest.Plan.Actions.Count(action => action is SyncAction.Upload),
                            latest.Plan.Actions.Count(action => action is SyncAction.DeleteLocal),
                            latest.Plan.Actions.Count(action => action is SyncAction.DeleteRemote),
                            conflicts.Count,
                            _clock.UtcNow - started,
                            outcome),
                        ct)
                    .ConfigureAwait(false);
                if (commit.IsFailure)
                {
                    return new SyncCycleResult(SyncCycleStatus.Failed, baseline, conflicts, apply.Value.Deferred.Count > 0, commit.Error);
                }

                _ = await _state.SaveRemoteTreesAsync(provider.CachedTrees, ct).ConfigureAwait(false);
                var status = Status(outcome, published);
                var result = new SyncCycleResult(
                    status,
                    commit.Value.Base,
                    conflicts,
                    apply.Value.Deferred.Count > 0);
                await _events.PublishAsync(
                    new SyncCompleted(_vault, outcome, commit.Value.Base, conflicts, result.HasDeferred),
                    ct).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new SyncCycleResult(SyncCycleStatus.Cancelled, baseline, [], false);
            }
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        await _events.PublishAsync(new FlushPendingEditsRequested(_vault), ct).ConfigureAwait(false);
        if (_flushEdits is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await _flushEdits(_vault, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }
    }

    private async Task<RemoteSnapshotProvider> ProviderAsync(CancellationToken ct)
    {
        if (_provider is not null)
        {
            return _provider;
        }

        var trees = await _state.LoadRemoteTreesAsync(ct).ConfigureAwait(false);
        _provider = new RemoteSnapshotProvider(_repository, trees);
        return _provider;
    }

    private async Task<Result<PreparedCycle>> PrepareAsync(
        SyncCycleRequest request,
        SyncBase baseline,
        HashCache cache,
        RemoteSnapshotProvider provider,
        CancellationToken ct)
    {
        var syncIgnore = await ReadSyncIgnoreAsync(ct).ConfigureAwait(false);
        if (syncIgnore.IsFailure)
        {
            return Result<PreparedCycle>.Failure(syncIgnore.Error);
        }

        var rules = IgnoreRules.Default
            .Then(syncIgnore.Value)
            .Then(IgnoreRules.Parse(request.Profile.Scope.ExtraIgnorePatterns ?? []));
        var local = await new LocalSnapshotScanner(_fileSystem, request.Profile.Scope, request.Profile.Policy, rules)
            .ScanAsync(cache, null, ct)
            .ConfigureAwait(false);
        var remote = await provider.GetAsync(ct).ConfigureAwait(false);
        if (remote.IsFailure)
        {
            return Result<PreparedCycle>.Failure(remote.Error);
        }

        var remoteIgnored = remote.Value.Manifest.Entries
            .Where(entry => SyncExclusions.IsIgnored(entry.Path, request.Profile.Scope, rules))
            .Select(entry => entry.Path)
            .ToList();
        var quarantined = remote.Value.Quarantined
            .Select(issue => VaultPath.Create(issue.Path))
            .Where(result => result.IsSuccess)
            .Select(result => result.Value)
            .ToList();
        var localExcluded = local.ExcludedPaths;
        var protectedPaths = localExcluded
            .Concat(remoteIgnored)
            .Concat(quarantined)
            .ToHashSet();

        var baselineFiltered = Remove(baseline.Manifest, protectedPaths);
        var localFiltered = Remove(local.Manifest, quarantined);
        var remoteFiltered = Remove(remote.Value.Manifest, localExcluded.Concat(remoteIgnored));
        var plan = SyncPlanner.Plan(
            new SyncBase(baseline.LastSyncedCommit, baseline.LastRemoteTree, baselineFiltered),
            localFiltered,
            remoteFiltered,
            request.Profile.Policy);
        var materialized = await new PlanMaterializer(_fileSystem, _repository)
            .MaterializeAsync(
                plan,
                localFiltered,
                remote.Value with { Manifest = remoteFiltered },
                request.Profile.Policy,
                _clock.UtcNow,
                _device.DisplayName,
                ct)
            .ConfigureAwait(false);
        return materialized.IsFailure
            ? Result<PreparedCycle>.Failure(materialized.Error)
            : new PreparedCycle(
                local with { Manifest = localFiltered },
                remote.Value,
                plan,
                materialized.Value,
                protectedPaths);
    }

    private async Task<Result<IgnoreRules>> ReadSyncIgnoreAsync(CancellationToken ct)
    {
        var path = VaultPath.Create(".basalt/syncignore").Value;
        var read = await _fileSystem.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        if (read.IsSuccess)
        {
            return IgnoreRules.Parse(read.Value);
        }

        return read.Error.Code == ErrorCodes.FsNotFound
            ? IgnoreRules.Empty
            : read.Error;
    }

    private static Manifest Remove(Manifest manifest, IEnumerable<VaultPath> paths)
    {
        var removed = paths as ICollection<VaultPath> ?? [.. paths];
        var entries = manifest.Entries.Where(entry => !removed.Contains(entry.Path));
        return Manifest.Create(entries).Value;
    }

    private string Message(SyncCycleRequest request, PreparedCycle cycle)
    {
        var target = cycle.Materialized.Target;
        if (cycle.Protected.Count > 0)
        {
            var entries = target.Entries.Concat(cycle.Remote.Manifest.Entries
                .Where(entry => cycle.Protected.Contains(entry.Path) && !target.Contains(entry.Path)));
            target = Manifest.Create(entries).Value;
        }

        return CommitMessageBuilder.Build(
            request.Profile.Commit,
            _device.DisplayName,
            _device.DeviceId.ToString(),
            request.BasaltVersion,
            request.VaultName,
            _clock.UtcNow,
            cycle.Remote.Manifest,
            cycle.Materialized with { Target = target },
            cycle.Materialized.Conflicts);
    }

    private static CommitAuthor Author(SyncCycleRequest request) => new(
        request.Profile.Commit.AuthorNameOverride ?? request.Author.Name,
        request.Profile.Commit.AuthorEmailOverride ?? request.Author.Email);

    private static SyncOutcome Outcome(PreparedCycle cycle, bool published)
    {
        if (cycle.Materialized.Conflicts.Count > 0)
        {
            return SyncOutcome.Merged;
        }

        if (published)
        {
            return SyncOutcome.Published;
        }

        return cycle.Plan.Actions.Any(action => action is SyncAction.Download or SyncAction.MergeText)
            ? SyncOutcome.Downloaded
            : SyncOutcome.UpToDate;
    }

    private static SyncCycleStatus Status(SyncOutcome outcome, bool published) => outcome switch
    {
        SyncOutcome.Merged => SyncCycleStatus.Merged,
        SyncOutcome.Published => SyncCycleStatus.Published,
        SyncOutcome.Downloaded => SyncCycleStatus.Downloaded,
        _ => published ? SyncCycleStatus.Published : SyncCycleStatus.UpToDate,
    };

    private sealed record PreparedCycle(
        LocalSnapshot Local,
        RemoteSnapshot Remote,
        SyncPlan Plan,
        MaterializedPlan Materialized,
        IReadOnlyCollection<VaultPath> Protected);
}
