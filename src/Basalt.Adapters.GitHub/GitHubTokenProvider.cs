using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Basalt.Application.Auth;
using Basalt.Application.Ports.Auth;
using Basalt.Application.Ports.Remote;
using Basalt.Application.Ports.Security;
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
    private readonly ISecretStore? _secrets;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<GitHubAccountId, StoredToken> _tokens = new();
    private readonly ConcurrentDictionary<GitHubAccountId, SemaphoreSlim> _locks = new();

    public GitHubTokenProvider(IGitHubAuthenticator authenticator)
        : this(authenticator, null, TimeProvider.System)
    {
    }

    public GitHubTokenProvider(IGitHubAuthenticator authenticator, TimeProvider clock)
        : this(authenticator, null, clock)
    {
    }

    public GitHubTokenProvider(IGitHubAuthenticator authenticator, ISecretStore? secrets, TimeProvider clock)
    {
        _authenticator = authenticator;
        _secrets = secrets;
        _clock = clock;
    }

    public async Task<Result<Unit>> StoreAsync(
        GitHubAccountId account,
        GitHubHost host,
        GitHubToken token,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(token);
        _tokens[account] = new StoredToken(host, token);
        if (_secrets is null)
        {
            return Result.Success();
        }

        var text = string.Join(
            '\n',
            Convert.ToBase64String(Encoding.UTF8.GetBytes(token.Value)),
            token.ExpiresAt?.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            token.RefreshToken is null ? string.Empty : Convert.ToBase64String(Encoding.UTF8.GetBytes(token.RefreshToken)));
        return await _secrets.SetAsync(Key(account), Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
    }

    public async Task<Result<Unit>> RemoveAsync(GitHubAccountId account, CancellationToken ct)
    {
        Invalidate(account);
        return _secrets is null
            ? Result.Success()
            : await _secrets.DeleteAsync(Key(account), ct).ConfigureAwait(false);
    }

    private static string Key(GitHubAccountId account) => "github.token." + account.Value.ToString("D");

    public void Store(GitHubAccountId account, GitHubHost host, GitHubToken token) =>
        _tokens[account] = new StoredToken(host, token);

    public void Invalidate(GitHubAccountId account) => _tokens.TryRemove(account, out _);

    public async Task<Result<GitHubToken>> GetTokenAsync(GitHubAccountId account, CancellationToken ct)
    {
        if (!_tokens.TryGetValue(account, out var stored))
        {
            if (_secrets is null)
            {
                return Result<GitHubToken>.Failure(
                    new Error(RemoteErrors.Unauthorized, "No token is stored for this account."));
            }

            var persisted = await _secrets.GetAsync(Key(account), ct).ConfigureAwait(false);
            if (persisted.IsFailure)
            {
                return Result<GitHubToken>.Failure(persisted.Error);
            }

            if (persisted.Value is not { } bytes || Decode(bytes) is not { } token)
            {
                return Result<GitHubToken>.Failure(
                    new Error(RemoteErrors.Unauthorized, "No token is stored for this account."));
            }

            stored = new StoredToken(GitHubHost.GitHubDotCom, token);
            _tokens[account] = stored;
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

    private static GitHubToken? Decode(byte[] bytes)
    {
        try
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            if (lines.Length == 0 || lines[0].Length == 0)
            {
                return null;
            }

            DateTimeOffset? expiry = lines.Length > 1
                && long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                    : null;
            var refresh = lines.Length > 2 && lines[2].Length > 0
                ? Encoding.UTF8.GetString(Convert.FromBase64String(lines[2]))
                : null;
            return new GitHubToken(Encoding.UTF8.GetString(Convert.FromBase64String(lines[0])), expiry, refresh);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record StoredToken(GitHubHost Host, GitHubToken Token);
}
