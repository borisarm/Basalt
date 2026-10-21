using System.Collections.Concurrent;
using System.Threading.Channels;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;

namespace Basalt.Application.Sync;

/// <summary>What to sync and the account/network facts the caller already resolved (spec 06 §11).</summary>
public sealed record SyncCycleContext(
    bool IsOnline,
    bool HasToken,
    CommitAuthor Author,
    string VaultName,
    string BasaltVersion = "1.0.0")
{
    public static SyncCycleContext Default { get; } = new(
        IsOnline: true,
        HasToken: true,
        new CommitAuthor("Basalt", "basalt@users.noreply.github.com"),
        "Vault");
}

/// <summary>Why a cycle was requested (spec 06 §11).</summary>
public enum SyncTriggerReason
{
    Manual,
    VaultOpen,
    Interval,
    EditIdle,
    AppBackground,
    Retry,
}

/// <summary>Observable state of one vault's sync (spec 06 §11; used by the sync status UI).</summary>
public sealed record SyncStatus(
    VaultId VaultId,
    bool IsRunning,
    SyncCycleResult? LastResult,
    DateTimeOffset? LastFinished,
    int ConsecutiveFailures,
    DateTimeOffset? NextAttempt);

/// <summary>Runs one cycle; implemented by <see cref="VaultSyncEngine"/> and by test doubles.</summary>
public interface IVaultSyncRunner
{
    Task<SyncCycleResult> RunAsync(SyncCycleRequest request, CancellationToken ct);
}

/// <summary>Creates a runner for a vault profile (the composition root wires the real engine).</summary>
public interface IVaultSyncRunnerFactory
{
    IVaultSyncRunner Create(SyncProfile profile);
}

/// <summary>
/// Per-vault trigger queue (spec 06 §11): coalesces requests, runs one cycle at a time per vault, starts vaults in
/// parallel and isolates failures. Interval/idle timers use <see cref="TimeProvider"/>; failed cycles retry with
/// exponential backoff.
/// </summary>
public sealed class SyncCoordinator : IAsyncDisposable
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    private static readonly SyncCycleContext DefaultContext = SyncCycleContext.Default;

    private readonly IVaultSyncRunnerFactory _factory;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<VaultId, State> _states = new();
    private readonly CancellationTokenSource _shutdown = new();
    private bool _disposed;

    public SyncCoordinator(IVaultSyncRunnerFactory factory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
        _time = time ?? TimeProvider.System;
    }

    public event Action<SyncStatus>? StatusChanged;

    /// <summary>Starts the worker and the automatic triggers; an open vault may sync immediately (spec 06 §11).</summary>
    public Task StartAsync(SyncProfile profile, SyncCycleContext? context = null)
    {
        var state = Get(profile, context);
        EnsureWorker(state);
        EnsureInterval(state);
        if (profile.Triggers.OnVaultOpen)
        {
            Queue(state, SyncTriggerReason.VaultOpen);
        }

        return Task.CompletedTask;
    }

    public async Task<SyncCycleResult?> RequestAsync(
        SyncProfile profile,
        SyncTriggerReason reason,
        SyncCycleContext? context = null,
        CancellationToken ct = default)
    {
        var state = Get(profile, context);
        EnsureWorker(state);
        var completion = new TaskCompletionSource<SyncCycleResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!TryQueue(state, reason, completion))
        {
            return null;
        }

        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        return await completion.Task.ConfigureAwait(false);
    }

    public SyncStatus GetStatus(VaultId vault) => ToStatus(GetState(vault));

    public void UpdateContext(VaultId vault, SyncCycleContext context)
    {
        var state = GetState(vault);
        state.Context = context;
    }

    /// <summary>After an edit saved by the user; syncs once the vault has been idle for the configured delay.</summary>
    public void NotifyEdit(VaultId vault)
    {
        var state = GetState(vault);
        var delay = state.Profile.Triggers.AfterEditIdle;
        if (delay is null)
        {
            return;
        }

        lock (state.Gate)
        {
            state.Idle?.Cancel();
            state.Idle?.Dispose();
            state.Idle = CancellationTokenSource.CreateLinkedTokenSource(state.Stop.Token);
            _ = IdleAsync(state, delay.Value, state.Idle.Token);
        }
    }

    public void NotifyAppBackground(VaultId vault)
    {
        var state = GetState(vault);
        if (state.Profile.Triggers.OnAppBackground)
        {
            Queue(state, SyncTriggerReason.AppBackground);
        }
    }

    public async Task StopAsync(VaultId vault)
    {
        if (!_states.TryRemove(vault, out var state))
        {
            return;
        }

        await state.Stop.CancelAsync().ConfigureAwait(false);
        state.Queue.Writer.TryComplete();
        if (state.Worker is { } worker)
        {
            await worker.ConfigureAwait(false);
        }

        state.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        foreach (var state in _states.Values)
        {
            state.Queue.Writer.TryComplete();
        }

        foreach (var state in _states.Values)
        {
            if (state.Worker is { } worker)
            {
                await worker.ConfigureAwait(false);
            }

            state.Dispose();
        }

        _shutdown.Dispose();
    }

    private State Get(SyncProfile profile, SyncCycleContext? context)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var state = _states.GetOrAdd(profile.VaultId, vault => new State(vault, profile, context ?? DefaultContext, _shutdown.Token));
        state.Profile = profile;
        if (context is not null)
        {
            state.Context = context;
        }

        return state;
    }

    private State GetState(VaultId vault) =>
        _states.TryGetValue(vault, out var state)
            ? state
            : throw new InvalidOperationException($"The vault {vault} is not configured in the sync coordinator.");

    private void EnsureWorker(State state)
    {
        lock (state.Gate)
        {
            state.Worker ??= Task.Run(() => WorkerAsync(state), CancellationToken.None);
        }
    }

    private async Task WorkerAsync(State state)
    {
        try
        {
            await foreach (var work in state.Queue.Reader.ReadAllAsync(state.Stop.Token).ConfigureAwait(false))
            {
                Interlocked.Exchange(ref state.Queued, 0);
                SyncCycleResult result;
                state.IsRunning = true;
                try
                {
                    var runner = _factory.Create(state.Profile);
                    var request = new SyncCycleRequest(
                        state.Profile,
                        state.Context.IsOnline,
                        state.Context.HasToken,
                        state.Context.Author,
                        state.Context.VaultName,
                        state.Context.BasaltVersion);
                    result = await runner.RunAsync(request, state.Stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (state.Stop.IsCancellationRequested)
                {
                    work.Completion?.TrySetCanceled(state.Stop.Token);
                    return;
                }
                catch (Exception exception)
                {
                    result = new SyncCycleResult(
                        SyncCycleStatus.Failed,
                        SyncBase.Empty,
                        [],
                        false,
                        new Error("sync.coordinator", exception.Message));
                }
                finally
                {
                    state.IsRunning = false;
                }

                state.LastResult = result;
                state.LastFinished = _time.GetUtcNow();
                if (result.Status == SyncCycleStatus.Failed)
                {
                    state.ConsecutiveFailures++;
                    ScheduleRetry(state, state.ConsecutiveFailures);
                }
                else
                {
                    state.ConsecutiveFailures = 0;
                    state.NextAttempt = null;
                    state.Retry?.Cancel();
                }

                work.Completion?.TrySetResult(result);
                RaiseChanged(state);
            }
        }
        catch (OperationCanceledException) when (state.Stop.IsCancellationRequested)
        {
        }
    }

    private void ScheduleRetry(State state, int failures)
    {
        lock (state.Gate)
        {
            state.Retry?.Cancel();
            state.Retry?.Dispose();
            state.Retry = CancellationTokenSource.CreateLinkedTokenSource(state.Stop.Token);
            var delay = TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Min(failures - 1, 20))));
            state.NextAttempt = _time.GetUtcNow() + delay;
            _ = RetryAsync(state, delay, state.Retry.Token);
        }
    }

    private async Task RetryAsync(State state, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Queue(state, SyncTriggerReason.Retry);
    }

    private void EnsureInterval(State state)
    {
        lock (state.Gate)
        {
            state.Interval?.Cancel();
            state.Interval?.Dispose();
            if (state.Profile.Triggers.Interval is not { } interval || interval <= TimeSpan.Zero)
            {
                state.Interval = null;
                return;
            }

            state.Interval = CancellationTokenSource.CreateLinkedTokenSource(state.Stop.Token);
            _ = IntervalAsync(state, interval, state.Interval.Token);
        }
    }

    private async Task IntervalAsync(State state, TimeSpan interval, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, _time, ct).ConfigureAwait(false);
                Queue(state, SyncTriggerReason.Interval);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task IdleAsync(State state, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            Queue(state, SyncTriggerReason.EditIdle);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Queue(State state, SyncTriggerReason reason) => TryQueue(state, reason, completion: null);

    private static bool TryQueue(State state, SyncTriggerReason reason, TaskCompletionSource<SyncCycleResult?>? completion)
    {
        if (Interlocked.CompareExchange(ref state.Queued, 1, 0) != 0)
        {
            completion?.TrySetResult(null);
            return false;
        }

        if (!state.Queue.Writer.TryWrite(new Work(reason, completion)))
        {
            Interlocked.Exchange(ref state.Queued, 0);
            completion?.TrySetResult(null);
            return false;
        }

        return true;
    }

    private void RaiseChanged(State state) => StatusChanged?.Invoke(ToStatus(state));

    private static SyncStatus ToStatus(State state) => new(
        state.VaultId,
        state.IsRunning,
        state.LastResult,
        state.LastFinished,
        state.ConsecutiveFailures,
        state.NextAttempt);

    private sealed record Work(SyncTriggerReason Reason, TaskCompletionSource<SyncCycleResult?>? Completion);

    private sealed class State : IDisposable
    {
        public State(VaultId vaultId, SyncProfile profile, SyncCycleContext context, CancellationToken shutdown)
        {
            VaultId = vaultId;
            Profile = profile;
            Context = context;
            Stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        }

        public VaultId VaultId { get; }

        public SyncProfile Profile { get; set; }

        public SyncCycleContext Context { get; set; }

        public CancellationTokenSource Stop { get; }

        public Channel<Work> Queue { get; } = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });

        public Task? Worker { get; set; }

        public CancellationTokenSource? Interval { get; set; }

        public CancellationTokenSource? Idle { get; set; }

        public CancellationTokenSource? Retry { get; set; }

        public int Queued;

        public bool IsRunning { get; set; }

        public SyncCycleResult? LastResult { get; set; }

        public DateTimeOffset? LastFinished { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? NextAttempt { get; set; }

        public Lock Gate { get; } = new();

        public void Dispose()
        {
            Stop.Dispose();
            Interval?.Dispose();
            Idle?.Dispose();
            Retry?.Dispose();
        }
    }
}
