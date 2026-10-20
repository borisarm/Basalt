using Basalt.Application.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Testing.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class GitHubTokenProviderTests
{
    private static readonly GitHubAccountId Account = new(Guid.NewGuid());
    private static readonly GitHubHost Host = GitHubHost.GitHubDotCom;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [Trait("Spec", "AUTH-TOKEN")]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        var provider = Create(new FakeTimeProvider(Now), out _);

        var token = await provider.GetTokenAsync(Account, Ct);

        token.Error.Code.Should().Be(RemoteErrors.Unauthorized);
    }

    [Fact]
    [Trait("Spec", "AUTH-TOKEN")]
    public async Task ValidToken_IsReturnedAsIs()
    {
        var provider = Create(new FakeTimeProvider(Now), out var authenticator);
        var token = new GitHubToken("token", Now.AddHours(1), "refresh");
        provider.Store(Account, Host, token);

        (await provider.GetTokenAsync(Account, Ct)).Value.Should().Be(token);
        authenticator.Polls.Should().Be(0);
    }

    [Fact]
    [Trait("Spec", "AUTH-TOKEN")]
    public async Task TokenNearExpiry_IsRefreshedFiveMinutesBefore()
    {
        var clock = new FakeTimeProvider(Now);
        var provider = Create(clock, out var authenticator);
        authenticator.RefreshedToken = new GitHubToken("refreshed", Now.AddHours(1), "refresh");
        provider.Store(Account, Host, new GitHubToken("old", Now.AddMinutes(2), "refresh"));

        var token = await provider.GetTokenAsync(Account, Ct);
        var cached = await provider.GetTokenAsync(Account, Ct);

        token.Value.Value.Should().Be("refreshed");
        cached.Value.Value.Should().Be("refreshed");
    }

    [Fact]
    [Trait("Spec", "AUTH-TOKEN")]
    public async Task ExpiredTokenWithoutRefresh_IsUnauthorized_AndInvalidateForgetsIt()
    {
        var provider = Create(new FakeTimeProvider(Now), out var authenticator);
        authenticator.RefreshedToken = new GitHubToken("refreshed");
        provider.Store(Account, Host, new GitHubToken("old", Now.AddMinutes(-1)));

        (await provider.GetTokenAsync(Account, Ct)).Error.Code.Should().Be(RemoteErrors.Unauthorized);
        provider.Store(Account, Host, new GitHubToken("token"));
        provider.Invalidate(Account);
        (await provider.GetTokenAsync(Account, Ct)).Error.Code.Should().Be(RemoteErrors.Unauthorized);
    }

    private static DateTimeOffset Now => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static GitHubTokenProvider Create(TimeProvider clock, out FakeGitHubAuthenticator authenticator)
    {
        authenticator = new FakeGitHubAuthenticator(Host);
        return new GitHubTokenProvider(authenticator, clock);
    }
}
