using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Application.Auth;

/// <summary>An OAuth access token and, for GitHub Apps, its refresh data (spec 06 §9.1).</summary>
public sealed record GitHubToken(string Value, DateTimeOffset? ExpiresAt = null, string? RefreshToken = null);

/// <summary>Profile returned by <c>GET /user</c>.</summary>
public sealed record GitHubUserInfo(long Id, string Login, string? Name, Uri? AvatarUrl, IReadOnlyList<string> Scopes);

/// <summary>Provides a valid token for an account, refreshing it when needed (spec 06 §9.1).</summary>
public interface IGitHubTokenProvider
{
    Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct);
}
