using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Basalt.Testing.Contracts;
using Basalt.Testing.Fakes;

namespace Basalt.Application.Tests.Auth;

public sealed class FakeAuthenticatorContractTests : AuthenticatorContract
{
    private readonly FakeGitHubAuthenticator _authenticator =
        new(GitHubHost.GitHubDotCom);

    protected override GitHubHost Host => GitHubHost.GitHubDotCom;

    protected override string Login => _authenticator.Login;

    protected override GitHubToken ValidToken => new("access-token");

    protected override GitHubToken RefreshableToken => new("access-token", RefreshToken: "refresh-token");

    protected override Task ConfigureAsync(AuthenticatorScenario scenario)
    {
        _authenticator.Scenario = scenario switch
        {
            AuthenticatorScenario.Pending => FakeAuthenticatorScenario.Pending,
            AuthenticatorScenario.SlowDown => FakeAuthenticatorScenario.SlowDown,
            AuthenticatorScenario.Approved => FakeAuthenticatorScenario.Approved,
            AuthenticatorScenario.Denied => FakeAuthenticatorScenario.Denied,
            _ => FakeAuthenticatorScenario.Expired,
        };
        return Task.CompletedTask;
    }

    protected override Task<IGitHubAuthenticator> CreateSutAsync() =>
        Task.FromResult<IGitHubAuthenticator>(_authenticator);
}
