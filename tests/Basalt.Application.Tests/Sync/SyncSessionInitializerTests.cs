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

public sealed class SyncSessionInitializerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly VaultId Vault = new(Guid.NewGuid());
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly RemoteRepositoryRef Ref = new(GitHubHost.GitHubDotCom, "acme", "notes", "main");

    private static SyncProfile Profile(bool paused = false) => new(
        Vault, Account, Ref, true, paused,
        new SyncTriggers(OnVaultOpen: false), SyncPolicy.Default, SyncScope.Default, new CommitSettings());

    [Fact]
    public async Task WithoutProfile_DoesNotAttachARepository()
    {
        var session = Session();
        var coordinator = Coordinator();
        var initializer = Initializer(new InMemorySyncProfileStore(), coordinator);

        var result = await initializer.InitializeAsync(session, Ct);

        result.IsSuccess.Should().BeTrue();
        session.TryGet<IRemoteRepository>().Should().BeNull();
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ConfiguredProfile_AttachesTheRepositoryAndStartsTheCoordinator()
    {
        var profiles = new InMemorySyncProfileStore();
        await profiles.SaveAsync(Profile(), Ct);
        var session = Session();
        var coordinator = Coordinator();
        var initializer = Initializer(profiles, coordinator);

        var result = await initializer.InitializeAsync(session, Ct);

        result.IsSuccess.Should().BeTrue();
        session.TryGet<IRemoteRepository>().Should().NotBeNull();
        coordinator.GetStatus(Vault).IsRunning.Should().BeFalse();
        await coordinator.DisposeAsync();
    }

    private static VaultSession Session() =>
        new(Vault, "Notes", new InMemoryVaultFileSystem(), VaultSettings.Default);

    private static SyncCoordinator Coordinator() =>
        new(new Factory(), new FakeTimeProvider(DateTimeOffset.UnixEpoch));

    private static SyncSessionInitializer Initializer(InMemorySyncProfileStore profiles, SyncCoordinator coordinator)
    {
        var tokens = Substitute.For<IGitHubTokenProvider>();
        tokens.GetTokenAsync(Account, Arg.Any<CancellationToken>())
            .Returns(Result<GitHubToken>.Success(new GitHubToken("token")));
        var accounts = Substitute.For<IAccountStore>();
        accounts.GetAsync(Account, Arg.Any<CancellationToken>())
            .Returns(Result<GitHubAccount>.Failure(new Error("account.not_found", "missing")));
        return new SyncSessionInitializer(profiles, new InMemoryRemoteRepositoryFactory(), tokens, accounts, coordinator);
    }

    private sealed class Factory : IVaultSyncRunnerFactory
    {
        public IVaultSyncRunner Create(SyncProfile profile) => throw new InvalidOperationException("No cycle expected.");
    }
}
