using Basalt.Application.Accounts;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Sync;
using Basalt.Application.Vaults;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Domain.Sync;
using Basalt.Domain.Vaults;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;


namespace Basalt.Application.Tests.Sync;

public sealed class SyncUseCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly DateTimeOffset When = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static SyncProfile Profile(bool paused = false) => new(
        Vault,
        Account,
        new RemoteRepositoryRef(GitHubHost.GitHubDotCom, "acme", "vault", "main"),
        Enabled: true,
        paused,
        new SyncTriggers(OnVaultOpen: false),
        SyncPolicy.Default,
        SyncScope.Default,
        new CommitSettings());

    private static SyncCoordinator Coordinator(RecordingRunner runner) =>
        new(new Factory(runner), new FakeTimeProvider(When));

    private static GitHubAccount AccountInfo() =>
        new(Account, "octocat", GitHubHost.GitHubDotCom, null, ["repo"], When);

    [Fact]
    public async Task Pause_SavesTheFlagAndStopsTheCoordinator()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(Profile(), Ct);
        var runner = new RecordingRunner(_ => Task.FromResult(UpToDate()));
        await using var coordinator = Coordinator(runner);
        await coordinator.StartAsync(Profile());
        var handler = new PauseSyncHandler(profiles, coordinator);

        var result = await handler.HandleAsync(new PauseSync(Vault), Ct);

        result.IsSuccess.Should().BeTrue();
        (await profiles.GetAsync(Vault, Ct))!.Paused.Should().BeTrue();
        var status = await new GetSyncStatusHandler(profiles, coordinator).HandleAsync(new GetSyncStatus(Vault), Ct);
        status.Value.State.Should().Be(SyncState.Paused);
        runner.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Resume_ClearsTheFlagAndRestartsTriggers()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(Profile(paused: true), Ct);
        await using var coordinator = Coordinator(new RecordingRunner(_ => Task.FromResult(UpToDate())));
        var handler = new ResumeSyncHandler(profiles, coordinator);

        var result = await handler.HandleAsync(new ResumeSync(Vault), Ct);

        result.IsSuccess.Should().BeTrue();
        var profile = await profiles.GetAsync(Vault, Ct);
        profile!.Paused.Should().BeFalse();
        profile.Enabled.Should().BeTrue();
        var status = await new GetSyncStatusHandler(profiles, coordinator).HandleAsync(new GetSyncStatus(Vault), Ct);
        status.Value.State.Should().Be(SyncState.Idle);
    }

    [Fact]
    public async Task Disable_DeletesProfileAndSyncStateButKeepsTheVault()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(Profile(), Ct);
        var states = new InMemorySyncStateStoreFactory();
        await states.Create(Vault).AppendLogAsync(LogEntry(), Ct);
        var coordinator = Coordinator(new RecordingRunner(_ => Task.FromResult(UpToDate())));
        await coordinator.StartAsync(Profile());
        var handler = new DisableSyncHandler(profiles, states, coordinator);

        var result = await handler.HandleAsync(new DisableSync(Vault), Ct);

        result.IsSuccess.Should().BeTrue();
        (await profiles.GetAsync(Vault, Ct)).Should().BeNull();
        (await states.Create(Vault).LoadLogAsync(10, Ct)).Should().BeEmpty();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Status_WithoutProfile_IsNotConfigured()
    {
        var profiles = new InMemorySyncProfileStore();
        await using var coordinator = Coordinator(new RecordingRunner(_ => Task.FromResult(UpToDate())));

        var status = await new GetSyncStatusHandler(profiles, coordinator).HandleAsync(new GetSyncStatus(Vault), Ct);

        status.Value.State.Should().Be(SyncState.NotConfigured);
        status.Value.LastResult.Should().BeNull();
    }

    [Fact]
    public async Task Log_ReturnsTheMostRecentEntries()
    {
        var states = new InMemorySyncStateStoreFactory();
        await states.Create(Vault).AppendLogAsync(LogEntry(), Ct);
        await states.Create(Vault).AppendLogAsync(LogEntry() with { Outcome = SyncOutcome.Merged }, Ct);

        var log = await new GetSyncLogHandler(states).HandleAsync(new GetSyncLog(Vault, 1), Ct);

        log.Value.Should().ContainSingle().Which.Outcome.Should().Be(SyncOutcome.Merged);
    }

    [Fact]
    public async Task SyncVault_WithoutProfile_ReturnsNotConfigured()
    {
        var profiles = new InMemorySyncProfileStore();
        var tokens = Substitute.For<IGitHubTokenProvider>();
        var accounts = Substitute.For<IAccountStore>();
        var registry = Substitute.For<IVaultRegistry>();
        await using var coordinator = Coordinator(new RecordingRunner(_ => Task.FromResult(UpToDate())));
        var handler = new SyncVaultHandler(profiles, tokens, accounts, registry, coordinator);

        var result = await handler.HandleAsync(new SyncVault(Vault), Ct);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(SyncErrors.NotConfigured);
    }

    [Fact]
    public async Task SyncVault_RunsWithTheAccountAuthorAndReportsTheOutcome()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(Profile(), Ct);
        var tokens = Substitute.For<IGitHubTokenProvider>();
        tokens.GetTokenAsync(Account, Arg.Any<CancellationToken>())
            .Returns(Result<GitHubToken>.Success(new GitHubToken("token")));
        var accounts = Substitute.For<IAccountStore>();
        accounts.GetAsync(Account, Arg.Any<CancellationToken>())
            .Returns(Result<GitHubAccount>.Success(AccountInfo()));
        var registry = Substitute.For<IVaultRegistry>();
        registry.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new VaultRegistration(Vault, "Notes", "C:\\vault", When)]);
        var runner = new RecordingRunner(_ => Task.FromResult(UpToDate()));
        await using var coordinator = Coordinator(runner);
        var handler = new SyncVaultHandler(profiles, tokens, accounts, registry, coordinator);

        var result = await handler.HandleAsync(new SyncVault(Vault), Ct);

        result.Value!.Status.Should().Be(SyncCycleStatus.UpToDate);
        runner.Requests.Should().ContainSingle();
        runner.Requests[0].HasToken.Should().BeTrue();
        runner.Requests[0].Author.Name.Should().Be("octocat");
        runner.Requests[0].VaultName.Should().Be("Notes");
    }

    private static SyncCycleResult UpToDate() => new(SyncCycleStatus.UpToDate, SyncBase.Empty, [], false);

    private static SyncLogEntry LogEntry() =>
        new(When, SyncOutcome.Downloaded, null, 1, 0, 0, 0, 0, 0, TimeSpan.FromSeconds(1));

    private sealed class RecordingRunner(Func<SyncCycleRequest, Task<SyncCycleResult>> handler) : IVaultSyncRunner
    {
        private readonly List<SyncCycleRequest> _requests = [];

        public List<SyncCycleRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        public int Calls => Requests.Count;

        public Task<SyncCycleResult> RunAsync(SyncCycleRequest request, CancellationToken ct)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            return handler(request);
        }
    }

    private sealed class Factory(RecordingRunner runner) : IVaultSyncRunnerFactory
    {
        public IVaultSyncRunner Create(SyncProfile profile) => runner;
    }
}
