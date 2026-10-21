using Basalt.Application.Accounts;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Basalt.Application.Tests.Accounts;

public sealed class AccountUseCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly GitHubHost Host = GitHubHost.GitHubDotCom;
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StartSignIn_ReturnsTheDeviceCode()
    {
        var authenticator = new FakeGitHubAuthenticator(Host);

        var challenge = await new StartGitHubSignInHandler(authenticator).HandleAsync(new StartGitHubSignIn(Host), Ct);

        challenge.Value.UserCode.Should().Be(FakeGitHubAuthenticator.UserCode);
        challenge.Value.VerificationUri.Should().Be("https://github.com/login/device");
    }

    [Fact]
    public async Task CompleteSignIn_SavesAccountAndStoresTheToken()
    {
        var authenticator = new FakeGitHubAuthenticator(Host);
        var tokens = TokenProvider();
        var accounts = Store();
        var handler = Handler(authenticator, tokens, accounts);
        var challenge = await authenticator.StartDeviceFlowAsync(Host, Ct);

        var account = await handler.HandleAsync(new CompleteGitHubSignIn(challenge.Value), Ct);

        account.Value.Login.Should().Be("basalt-test");
        (await accounts.GetAllAsync(Ct)).Should().ContainSingle();
        await tokens.Received(1).StoreAsync(
            account.Value.Id, Host, Arg.Is<GitHubToken>(token => token.Value == "access-token"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteSignIn_Denied_ReturnsAccessDenied()
    {
        var authenticator = new FakeGitHubAuthenticator(Host) { Scenario = FakeAuthenticatorScenario.Denied };
        var handler = Handler(authenticator, TokenProvider(), Store());

        var result = await handler.HandleAsync(
            new CompleteGitHubSignIn((await authenticator.StartDeviceFlowAsync(Host, Ct)).Value), Ct);

        result.Error.Code.Should().Be(AuthErrors.AccessDenied);
    }

    [Fact]
    public async Task CompleteSignIn_PendingThenApproved_KeepsPolling()
    {
        var authenticator = new PendingOnceAuthenticator();
        var handler = Handler(authenticator, TokenProvider(), Store());
        var challenge = await authenticator.StartDeviceFlowAsync(Host, Ct);

        var result = await handler.HandleAsync(new CompleteGitHubSignIn(challenge.Value), Ct);

        result.IsSuccess.Should().BeTrue();
        authenticator.Polls.Should().Be(2);
    }

    [Fact]
    public async Task RemoveAccount_DeletesTheAccountAndItsToken()
    {
        var tokens = TokenProvider();
        var accounts = Store();
        var account = new GitHubAccount(new GitHubAccountId(Guid.NewGuid()), "octocat", Host, null, ["repo"], Now);
        await accounts.SaveAsync(account, Ct);

        var result = await new RemoveAccountHandler(accounts, tokens).HandleAsync(new RemoveAccount(account.Id), Ct);

        result.IsSuccess.Should().BeTrue();
        (await accounts.GetAllAsync(Ct)).Should().BeEmpty();
        await tokens.Received(1).RemoveAsync(account.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListRepositories_UsesTheAdministrationPort()
    {
        var admin = new InMemoryRepositoryAdministration(
            new RemoteRepositoryInfo("acme", "notes", "main", true, false, true));

        var result = await new ListRepositoriesHandler(admin)
            .HandleAsync(new ListRepositories(new GitHubAccountId(Guid.NewGuid())), Ct);

        result.Value.Should().ContainSingle().Which.FullName.Should().Be("acme/notes");
    }

    [Fact]
    public async Task CompleteSignIn_Expired_ReturnsExpiredToken()
    {
        var authenticator = new FakeGitHubAuthenticator(Host) { Scenario = FakeAuthenticatorScenario.Expired };
        var handler = Handler(authenticator, TokenProvider(), Store());
        var challenge = await authenticator.StartDeviceFlowAsync(Host, Ct);

        var result = await handler.HandleAsync(new CompleteGitHubSignIn(challenge.Value), Ct);

        result.Error.Code.Should().Be(AuthErrors.ExpiredToken);
    }

    [Fact]
    public async Task ListAccounts_ReturnsTheStoredAccounts()
    {
        var accounts = Store();
        await accounts.SaveAsync(
            new GitHubAccount(new GitHubAccountId(Guid.NewGuid()), "octocat", Host, null, ["repo"], Now), Ct);

        var result = await new ListAccountsHandler(accounts).HandleAsync(new ListAccounts(), Ct);

        result.Value.Should().ContainSingle().Which.Login.Should().Be("octocat");
    }

    [Fact]
    public async Task CreateRepositoryForVault_UsesTheAdministrationPort()
    {
        var admin = new InMemoryRepositoryAdministration();
        var account = new GitHubAccountId(Guid.NewGuid());

        var result = await new CreateRepositoryForVaultHandler(admin)
            .HandleAsync(new CreateRepositoryForVault(account, "acme", "notes"), Ct);

        result.Value.FullName.Should().Be("acme/notes");
        result.Value.IsPrivate.Should().BeTrue();
    }
    private static CompleteGitHubSignInHandler Handler(
        IGitHubAuthenticator authenticator,
        IGitHubTokenProvider tokens,
        IAccountStore accounts) =>
        new(authenticator, tokens, accounts, new SequentialIdGenerator(), new FakeClock(), new FakeTimeProvider(Now));

    private static IGitHubTokenProvider TokenProvider()
    {
        var tokens = Substitute.For<IGitHubTokenProvider>();
        tokens.StoreAsync(Arg.Any<GitHubAccountId>(), Arg.Any<GitHubHost>(), Arg.Any<GitHubToken>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        tokens.RemoveAsync(Arg.Any<GitHubAccountId>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        return tokens;
    }

    private static AccountStore Store() =>
        new(new InMemoryStore<AccountsDocument>(() => AccountsDocument.Empty));

    private sealed class PendingOnceAuthenticator : IGitHubAuthenticator
    {
        private readonly FakeGitHubAuthenticator _inner = new(Host);
        private int _polls;

        public int Polls => _polls;

        public Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct) =>
            Task.FromResult(Result<DeviceCodeChallenge>.Success(new DeviceCodeChallenge(
                host, "device", "USER-CODE", "https://github.com/login/device", TimeSpan.Zero, DateTimeOffset.MaxValue)));

        public Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct) =>
            Interlocked.Increment(ref _polls) == 1
                ? Task.FromResult(Result<GitHubToken>.Failure(new Error(AuthErrors.AuthorizationPending, "pending")))
                : _inner.PollForTokenAsync(challenge, ct);

        public Task<Result<GitHubUserInfo>> GetUserAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
            _inner.GetUserAsync(host, token, ct);

        public Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
            _inner.RefreshAsync(host, token, ct);
    }
}
