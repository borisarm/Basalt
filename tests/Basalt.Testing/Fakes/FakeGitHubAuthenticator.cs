using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Testing.Fakes;

public enum FakeAuthenticatorScenario
{
    Pending,
    SlowDown,
    Approved,
    Denied,
    Expired,
}

/// <summary>Deterministic device-flow authenticator for application tests (spec 06 §9.1).</summary>
public sealed class FakeGitHubAuthenticator : IGitHubAuthenticator
{
    public const string DeviceCode = "device-code";
    public const string UserCode = "ABCD-1234";

    public FakeGitHubAuthenticator(GitHubHost host)
    {
        Host = host;
    }

    public GitHubHost Host { get; }

    public FakeAuthenticatorScenario Scenario { get; set; } = FakeAuthenticatorScenario.Approved;

    public string Login { get; set; } = "basalt-test";

    public GitHubToken Token { get; set; } = new("access-token", DateTimeOffset.UtcNow.AddHours(1), "refresh-token");

    public GitHubToken RefreshedToken { get; set; } = new("refreshed-token", DateTimeOffset.UtcNow.AddHours(1), "refresh-token");

    public int Polls { get; private set; }

    public Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct) =>
        Task.FromResult(Result<DeviceCodeChallenge>.Success(new DeviceCodeChallenge(
            host, DeviceCode, UserCode, "https://github.com/login/device", TimeSpan.FromSeconds(5),
            DateTimeOffset.UtcNow.AddMinutes(15))));

    public Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct)
    {
        Polls++;
        return Task.FromResult(Scenario switch
        {
            FakeAuthenticatorScenario.Pending => Result<GitHubToken>.Failure(
                new Error(AuthErrors.AuthorizationPending, "pending")),
            FakeAuthenticatorScenario.SlowDown => Result<GitHubToken>.Failure(
                new Error(AuthErrors.SlowDown, "slow_down",
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["interval"] = 10 })),
            FakeAuthenticatorScenario.Approved => Result<GitHubToken>.Success(Token),
            FakeAuthenticatorScenario.Denied => Result<GitHubToken>.Failure(
                new Error(AuthErrors.AccessDenied, "denied")),
            _ => Result<GitHubToken>.Failure(new Error(AuthErrors.ExpiredToken, "expired")),
        });
    }

    public Task<Result<GitHubUserInfo>> GetUserAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
        Task.FromResult(token.Value is "access-token" or "refreshed-token"
            ? Result<GitHubUserInfo>.Success(new GitHubUserInfo(42, Login, "Basalt Test", null, ["repo"]))
            : Result<GitHubUserInfo>.Failure(new Error(AuthErrors.Unauthorized, "invalid token")));

    public Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct) =>
        Task.FromResult(token.RefreshToken is null
            ? Result<GitHubToken>.Failure(new Error(AuthErrors.Unauthorized, "cannot refresh"))
            : Result<GitHubToken>.Success(RefreshedToken));
}
