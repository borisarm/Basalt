using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;
using Basalt.Testing.Contracts;
using Basalt.Testing.FakeGitHub;

namespace Basalt.Adapters.GitHub.Tests;

public sealed class GitHubAuthenticatorTests : AuthenticatorContract
{
    private FakeGitHubServer? _server;

    protected override GitHubHost Host =>
        GitHubHost.ForTesting(new Uri(_server!.BaseAddress));

    protected override string Login => _server!.Options.Login;

    protected override GitHubToken ValidToken => new(_server!.Options.Token);

    protected override GitHubToken RefreshableToken => new(_server!.Options.Token, RefreshToken: "refresh-token");

    protected override Task ConfigureAsync(AuthenticatorScenario scenario)
    {
        var flow = _server!.DeviceFlow;
        flow.Reset();
        flow.SlowDownPolls.Clear();
        flow.Outcome = FakeDeviceFlowOutcome.Approve;
        flow.ApproveAfterPolls = 1;
        switch (scenario)
        {
            case AuthenticatorScenario.Pending:
                flow.ApproveAfterPolls = 5;
                break;
            case AuthenticatorScenario.SlowDown:
                flow.SlowDownPolls.Add(1);
                flow.ApproveAfterPolls = 2;
                break;
            case AuthenticatorScenario.Denied:
                flow.Outcome = FakeDeviceFlowOutcome.Deny;
                break;
            case AuthenticatorScenario.Expired:
                flow.Outcome = FakeDeviceFlowOutcome.Expire;
                break;
        }

        return Task.CompletedTask;
    }

    protected override async Task<IGitHubAuthenticator> CreateSutAsync()
    {
        _server = await FakeGitHubServer.StartAsync(cancellationToken: Ct);
        return new AuthenticatorSut(
            new GitHubDeviceFlowAuthenticator(new HttpClient(), "basalt-client"),
            _server);
    }

    private sealed class AuthenticatorSut(IGitHubAuthenticator inner, FakeGitHubServer server)
        : IGitHubAuthenticator, IAsyncDisposable
    {
        public Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct) =>
            inner.StartDeviceFlowAsync(host, ct);

        public Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct) =>
            inner.PollForTokenAsync(challenge, ct);

        public Task<Result<GitHubUserInfo>> GetUserAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
            inner.GetUserAsync(host, token, ct);

        public Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
            inner.RefreshAsync(host, token, ct);

        public async ValueTask DisposeAsync() => await server.DisposeAsync();
    }
}
