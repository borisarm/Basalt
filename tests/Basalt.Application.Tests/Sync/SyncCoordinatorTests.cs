using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Domain.Accounts;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Application.Tests.Sync;

public sealed class SyncCoordinatorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SyncProfile Profile(VaultId vault, SyncTriggers? triggers = null) => new(
        vault,
        new GitHubAccountId(Guid.NewGuid()),
        new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"),
        Enabled: true,
        Paused: false,
        triggers ?? new SyncTriggers(),
        SyncPolicy.Default,
        SyncScope.Default,
        new CommitSettings());

    private static SyncCycleResult Result(SyncCycleStatus status = SyncCycleStatus.UpToDate) =>
        new(status, SyncBase.Empty, [], false);

    private static FakeTimeProvider Time() => new(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));

    private sealed class Runner(Func<SyncCycleRequest, CancellationToken, Task<SyncCycleResult>> handler) : IVaultSyncRunner
    {
        private readonly SemaphoreSlim _calls = new(0);
        private int _count;

        public int Calls => Volatile.Read(ref _count);

        public Func<SyncCycleRequest, CancellationToken, Task<SyncCycleResult>> Handler { get; set; } = handler;

        public Task WaitForCallAsync(CancellationToken ct) => _calls.WaitAsync(ct);

        public async Task<SyncCycleResult> RunAsync(SyncCycleRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            _calls.Release();
            return await Handler(request, ct);
        }
    }

    private static SyncCoordinator Coordinator(Func<SyncProfile, IVaultSyncRunner> create, TimeProvider time) =>
        new(new Factory(create), time);

    private sealed class Factory(Func<SyncProfile, IVaultSyncRunner> create) : IVaultSyncRunnerFactory
    {
        public IVaultSyncRunner Create(SyncProfile profile) => create(profile);
    }

    [Fact]
    public async Task Start_OnVaultOpen_RunsACycle()
    {
        var runner = new Runner((_, _) => Task.FromResult(Result()));
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, Time());

        await coordinator.StartAsync(Profile(vault));
        await runner.WaitForCallAsync(Ct);
        await EventuallyAsync(() => coordinator.GetStatus(vault).LastResult is not null, Ct);

        coordinator.GetStatus(vault).LastResult!.Status.Should().Be(SyncCycleStatus.UpToDate);
    }

    [Fact]
    public async Task Request_CoalescesWhileARunIsInProgressAndRunsOneFollowUp()
    {
        var first = new TaskCompletionSource<SyncCycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var runner = new Runner((_, _) => Interlocked.Increment(ref calls) == 1
            ? first.Task
            : Task.FromResult(Result()));
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, Time());
        var profile = Profile(vault);

        var running = coordinator.RequestAsync(profile, SyncTriggerReason.Manual, null, Ct);
        await runner.WaitForCallAsync(Ct);
        var queued = coordinator.RequestAsync(profile, SyncTriggerReason.Manual, null, Ct);
        var coalesced = await coordinator.RequestAsync(profile, SyncTriggerReason.Manual, null, Ct);

        coalesced.Should().BeNull();
        first.SetResult(Result());
        (await running)!.Status.Should().Be(SyncCycleStatus.UpToDate);
        (await queued)!.Status.Should().Be(SyncCycleStatus.UpToDate);
        runner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Request_IsolatesFailuresBetweenVaults()
    {
        var vaultA = new VaultId(Guid.NewGuid());
        var vaultB = new VaultId(Guid.NewGuid());
        var runnerA = new Runner((_, _) => throw new InvalidOperationException("boom"));
        var runnerB = new Runner((_, _) => Task.FromResult(Result()));
        await using var coordinator = Coordinator(
            profile => profile.VaultId == vaultA ? runnerA : runnerB,
            Time());

        var a = await coordinator.RequestAsync(Profile(vaultA), SyncTriggerReason.Manual, null, Ct);
        var b = await coordinator.RequestAsync(Profile(vaultB), SyncTriggerReason.Manual, null, Ct);

        a!.Status.Should().Be(SyncCycleStatus.Failed);
        a.Error!.Code.Should().Be("sync.coordinator");
        b!.Status.Should().Be(SyncCycleStatus.UpToDate);
        coordinator.GetStatus(vaultA).ConsecutiveFailures.Should().Be(1);
        coordinator.GetStatus(vaultB).ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public async Task FailedCycle_RetriesWithBackoffAndResetsOnSuccess()
    {
        var calls = 0;
        var runner = new Runner((_, _) => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? Result(SyncCycleStatus.Failed) : Result()));
        var time = Time();
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, time);
        var profile = Profile(vault);

        var first = await coordinator.RequestAsync(profile, SyncTriggerReason.Manual, null, Ct);

        first!.Status.Should().Be(SyncCycleStatus.Failed);
        coordinator.GetStatus(vault).ConsecutiveFailures.Should().Be(1);
        coordinator.GetStatus(vault).NextAttempt.Should().NotBeNull();

        time.Advance(TimeSpan.FromSeconds(1));
        await runner.WaitForCallAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await EventuallyAsync(() => coordinator.GetStatus(vault).ConsecutiveFailures == 0, Ct);

        coordinator.GetStatus(vault).ConsecutiveFailures.Should().Be(0);
        coordinator.GetStatus(vault).LastResult!.Status.Should().Be(SyncCycleStatus.UpToDate);
        coordinator.GetStatus(vault).NextAttempt.Should().BeNull();
    }

    private static async Task EventuallyAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Start_WithAnInterval_RunsOnTheConfiguredSchedule()
    {
        var runner = new Runner((_, _) => Task.FromResult(Result()));
        var time = Time();
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, time);
        await coordinator.StartAsync(Profile(vault, new SyncTriggers(OnVaultOpen: false, Interval: TimeSpan.FromMinutes(1))));

        runner.Calls.Should().Be(0);
        time.Advance(TimeSpan.FromMinutes(1));
        await runner.WaitForCallAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        time.Advance(TimeSpan.FromMinutes(1));
        await runner.WaitForCallAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);

        runner.Calls.Should().Be(2);
    }

    [Fact]
    public async Task NotifyEdit_WaitsForTheIdleDelay()
    {
        var runner = new Runner((_, _) => Task.FromResult(Result()));
        var time = Time();
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, time);
        await coordinator.StartAsync(Profile(vault, new SyncTriggers(OnVaultOpen: false, AfterEditIdle: TimeSpan.FromSeconds(30))));

        coordinator.NotifyEdit(vault);
        time.Advance(TimeSpan.FromSeconds(20));
        await Task.Delay(50, Ct);
        runner.Calls.Should().Be(0);

        time.Advance(TimeSpan.FromSeconds(15));
        await runner.WaitForCallAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        runner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task NotifyAppBackground_RunsWhenEnabled()
    {
        var runner = new Runner((_, _) => Task.FromResult(Result()));
        var vault = new VaultId(Guid.NewGuid());
        await using var coordinator = Coordinator(_ => runner, Time());
        await coordinator.StartAsync(Profile(vault, new SyncTriggers(OnVaultOpen: false, OnAppBackground: true)));

        coordinator.NotifyAppBackground(vault);
        await runner.WaitForCallAsync(Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await EventuallyAsync(() => coordinator.GetStatus(vault).LastResult is not null, Ct);

        runner.Calls.Should().Be(1);
        coordinator.GetStatus(vault).LastResult!.Status.Should().Be(SyncCycleStatus.UpToDate);
    }
}
