using System.Collections.Concurrent;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Domain.Accounts;
using Basalt.Domain.Common;

namespace Basalt.Adapters.GitHub;

/// <summary>
/// Caches tokens per account and refreshes them five minutes before expiry when GitHub supplied a refresh token
/// (spec 06 §9.1).
/// </summary>
internal sealed class GitHubTokenProvider : IGitHubTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly IGitHubAuthenticator _authenticator;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<GitHubAccountId, StoredToken> _tokens = new();
    private readonly ConcurrentDictionary<GitHubAccountId, SemaphoreSlim> _locks = new();

    public GitHubTokenProvider(IGitHubAuthenticator authenticator)
        : this(authenticator, TimeProvider.System)
    {
    }

    public GitHubTokenProvider(IGitHubAuthenticator authenticator, TimeProvider clock)
    {
        _authenticator = authenticator;
        _clock = clock;
    }

    public void Store(GitHubAccountId account, GitHubHost host, GitHubToken token) =>
        _tokens[account] = new StoredToken(host, token);

    public void Invalidate(GitHubAccountId account) => _tokens.TryRemove(account, out _);

    public async Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct)
    {
        if (!_tokens.TryGetValue(account, out var stored))
        {
            return Result<GitHubToken>.Failure(
                new Error(RemoteErrors.Unauthorized, "No token is stored for this account."));
        }

        if (!NeedsRefresh(stored.Token))
        {
            return Result<GitHubToken>.Success(stored.Token);
        }

        var gate = _locks.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_tokens.TryGetValue(account, out stored))
            {
                return Result<GitHubToken>.Failure(
                    new Error(RemoteErrors.Unauthorized, "No token is stored for this account."));
            }

            if (!NeedsRefresh(stored.Token))
            {
                return Result<GitHubToken>.Success(stored.Token);
            }

            if (stored.Token.RefreshToken is null)
            {
                return stored.Token.ExpiresAt is { } expiry && expiry <= _clock.GetUtcNow()
                    ? Result<GitHubToken>.Failure(
                        new Error(RemoteErrors.Unauthorized, "The token expired and cannot be refreshed."))
                    : Result<GitHubToken>.Success(stored.Token);
            }

            var refreshed = await _authenticator.RefreshAsync(stored.Host, stored.Token, ct).ConfigureAwait(false);
            if (refreshed.IsFailure)
            {
                return Result<GitHubToken>.Failure(refreshed.Error);
            }

            _tokens[account] = new StoredToken(stored.Host, refreshed.Value);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool NeedsRefresh(GitHubToken token) =>
        token.ExpiresAt is { } expiry && expiry - RefreshMargin <= _clock.GetUtcNow();

    private sealed record StoredToken(GitHubHost Host, GitHubToken Token);
}
