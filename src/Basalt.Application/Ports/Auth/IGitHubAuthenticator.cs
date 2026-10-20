using Basalt.Application.Auth;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Application.Ports.Auth;

/// <summary>State of an OAuth Device Flow (spec 06 §9.1).</summary>
public sealed record DeviceCodeChallenge(
    GitHubHost Host,
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    TimeSpan Interval,
    DateTimeOffset ExpiresAt);

/// <summary>Error codes produced while polling the device flow.</summary>
public static class AuthErrors
{
    public const string AuthorizationPending = "auth.authorization_pending";
    public const string SlowDown = "auth.slow_down";
    public const string AccessDenied = "auth.access_denied";
    public const string ExpiredToken = "auth.expired_token";
    public const string InvalidResponse = "auth.invalid_response";
    public const string Unauthorized = "auth.unauthorized";
}

/// <summary>OAuth Device Flow and token refresh against a GitHub host (spec 03 §1.5, 06 §9.1).</summary>
public interface IGitHubAuthenticator
{
    Task<Result<DeviceCodeChallenge>> StartDeviceFlowAsync(GitHubHost host, CancellationToken ct);

    /// <summary>One poll. Pending and slow-down are expected errors; the caller respects the returned interval.</summary>
    Task<Result<GitHubToken>> PollForTokenAsync(DeviceCodeChallenge challenge, CancellationToken ct);

    Task<Result<GitHubUserInfo>> GetUserAsync(GitHubHost host, GitHubToken token, CancellationToken ct);

    Task<Result<GitHubToken>> RefreshAsync(GitHubHost host, GitHubToken token, CancellationToken ct);
}
